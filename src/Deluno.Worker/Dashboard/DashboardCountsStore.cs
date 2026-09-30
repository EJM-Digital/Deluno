using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Worker.Dashboard;

/// <summary>
/// The count-only reads behind the Dashboard that no repository offers.
///
/// <para>Each is one aggregate query over an indexed table on a read-only
/// connection: no row travels to be counted in memory, and none of them
/// leaves the database. They sit together, rather than as a method on each
/// repository, because the Dashboard is the only caller and because a summary
/// that reads a dozen tables needs one obvious place to look when it is slow.</para>
/// </summary>
public sealed class DashboardCountsStore(IDelunoDatabaseConnectionFactory databases)
{
    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ jobs

    public async Task<DispatchDayCounts> GetDispatchDayAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COALESCE(SUM(CASE WHEN grab_status = 'succeeded' AND grab_attempted_utc >= @since THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN detected_utc >= @since THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN import_status = 'imported' AND import_completed_utc >= @since THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN (grab_status = 'failed' AND grab_attempted_utc >= @since)
                                    OR (import_status = 'failed' AND import_completed_utc >= @since) THEN 1 ELSE 0 END), 0)
            FROM download_dispatches
            WHERE created_utc >= @since;
            """;
        Add(command, "@since", Iso(sinceUtc));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DispatchDayCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))
            : new DispatchDayCounts(0, 0, 0, 0);
    }

    /// <summary>Downloads that ended in a failure and are still on the books (not archived).</summary>
    public async Task<int> CountFailedDownloadsAsync(CancellationToken cancellationToken)
        => await ScalarAsync(
            DelunoDatabaseNames.Jobs,
            """
            SELECT COUNT(*) FROM download_dispatches
            WHERE archived_utc IS NULL AND (grab_status = 'failed' OR import_status = 'failed');
            """,
            cancellationToken);

    public async Task<int> CountFailedImportJobsAsync(CancellationToken cancellationToken)
        => await ScalarAsync(
            DelunoDatabaseNames.Jobs,
            """
            SELECT COUNT(*) FROM job_queue
            WHERE job_type = 'filesystem.import.execute' AND status IN ('failed', 'dead-letter');
            """,
            cancellationToken);

    public async Task<int> CountBlockedReleasesAsync(CancellationToken cancellationToken)
        => await ScalarAsync(DelunoDatabaseNames.Jobs, "SELECT COUNT(*) FROM blocked_releases;", cancellationToken);

    // ------------------------------------------------------------ recovery

    /// <summary>Open recovery cases by failure kind, both engines together.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetOpenRecoveryByKindAsync(CancellationToken cancellationToken)
    {
        var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (database, table) in new[]
                 {
                     (DelunoDatabaseNames.Movies, "movie_import_recovery_cases"),
                     (DelunoDatabaseNames.Series, "series_import_recovery_cases")
                 })
        {
            await using var connection = await databases.OpenReadOnlyConnectionAsync(database, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT failure_kind, COUNT(*) FROM {table} WHERE status = 'open' GROUP BY failure_kind;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(0);
                kinds[kind] = (kinds.TryGetValue(kind, out var existing) ? existing : 0) + reader.GetInt32(1);
            }
        }

        return kinds;
    }

    // ------------------------------------------------------------ processors

    public async Task<ProcessorCounts> GetProcessorCountsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        int total = 0, healthy = 0, failing = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN health_status = 'healthy' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN health_status NOT IN ('healthy', 'unknown', 'untested') THEN 1 ELSE 0 END), 0)
                FROM processor_connections WHERE is_enabled = 1;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt32(0);
                healthy = reader.GetInt32(1);
                failing = reader.GetInt32(2);
            }
        }

        int today = 0, waiting = 0, failedHandoffs = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    COALESCE(SUM(CASE WHEN created_utc >= @since THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status IN ('queued', 'scheduled', 'working') THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status IN ('failed', 'rejected') THEN 1 ELSE 0 END), 0)
                FROM processor_handoffs;
                """;
            Add(command, "@since", Iso(sinceUtc));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                today = reader.GetInt32(0);
                waiting = reader.GetInt32(1);
                failedHandoffs = reader.GetInt32(2);
            }
        }

        return new ProcessorCounts(total, healthy, failing, today, waiting, failedHandoffs);
    }

    // ------------------------------------------------------------- retention

    public async Task<RetentionCounts> GetRetentionCountsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        int leaving = 0, ready = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COALESCE(SUM(CASE WHEN state = 'leaving' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN state = 'ready' THEN 1 ELSE 0 END), 0)
                FROM retention_marks;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                leaving = reader.GetInt32(0);
                ready = reader.GetInt32(1);
            }
        }

        var next = new List<LeavingTitle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT entity_id, title, level, remove_after_utc
                FROM retention_marks
                WHERE state = 'leaving'
                ORDER BY remove_after_utc ASC
                LIMIT 3;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                next.Add(new LeavingTitle(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
            }
        }

        // Every rule together. A preview run removed nothing, so it adds nothing.
        int removed = 0;
        long bytes = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COALESCE(SUM(removed_count), 0), COALESCE(SUM(bytes), 0)
                FROM retention_runs
                WHERE kind <> 'preview' AND started_utc >= @since;
                """;
            Add(command, "@since", Iso(sinceUtc));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                removed = reader.GetInt32(0);
                bytes = reader.GetInt64(1);
            }
        }

        return new RetentionCounts(leaving, next, ready, removed, bytes);
    }

    // -------------------------------------------------------------- requests

    public async Task<RequestCounts> GetRequestCountsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        int servers = 0, healthy = 0, failing = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN health_status = 'healthy' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN health_status NOT IN ('healthy', 'unknown', 'untested') THEN 1 ELSE 0 END), 0)
                FROM seerr_servers WHERE is_enabled = 1;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                servers = reader.GetInt32(0);
                healthy = reader.GetInt32(1);
                failing = reader.GetInt32(2);
            }
        }

        var acquisitions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, COUNT(*) FROM seerr_acquisitions WHERE created_utc >= @since GROUP BY status;";
            Add(command, "@since", Iso(sinceUtc));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                acquisitions[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        var lists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT last_sync_status, COUNT(*) FROM intake_sources GROUP BY last_sync_status;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lists[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return new RequestCounts(servers, healthy, failing, acquisitions, lists);
    }

    // --------------------------------------------------------------- helpers

    private async Task<int> ScalarAsync(string database, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(database, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed record DispatchDayCounts(int Grabbed, int Detected, int Imported, int Failed);

public sealed record ProcessorCounts(int Total, int Healthy, int Failing, int HandoffsToday, int Waiting, int FailedHandoffs);

public sealed record LeavingTitle(string EntityId, string Title, string Level, DateTimeOffset RemoveAfterUtc);

public sealed record RetentionCounts(int Leaving, IReadOnlyList<LeavingTitle> Next, int Ready, int Removed, long RemovedBytes);

public sealed record RequestCounts(
    int Servers,
    int Healthy,
    int Failing,
    IReadOnlyDictionary<string, int> AcquisitionsByStatus,
    IReadOnlyDictionary<string, int> ImportListsByLastSync);
