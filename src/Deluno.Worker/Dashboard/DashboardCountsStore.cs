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

    // --------------------------------------------------------- library mix

    /// <summary>
    /// What the tracked files are: their quality tier and video codec, counted per
    /// engine from the stored wanted-state rows (one grouped query each), plus how
    /// many files each library holds (the subtitle panel's "held" base).
    /// </summary>
    public async Task<LibraryMixCounts> GetLibraryMixAsync(CancellationToken cancellationToken)
    {
        var movieQuality = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var movieCodec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var showQuality = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var showCodec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filesByLibrary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await using (var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken))
        {
            await GroupAsync(connection, "SELECT COALESCE(current_quality, ''), COUNT(*) FROM movie_wanted_state WHERE has_file = 1 GROUP BY 1;", movieQuality, cancellationToken);
            await GroupAsync(connection, "SELECT COALESCE(video_codec, ''), COUNT(*) FROM movie_wanted_state WHERE has_file = 1 GROUP BY 1;", movieCodec, cancellationToken);
            await GroupAsync(connection, "SELECT library_id, COUNT(*) FROM movie_wanted_state WHERE has_file = 1 GROUP BY library_id;", filesByLibrary, cancellationToken);
        }

        await using (var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Series, cancellationToken))
        {
            await GroupAsync(connection, "SELECT COALESCE(w.current_quality, ''), COUNT(*) FROM episode_wanted_state w JOIN episode_entries e ON e.id = w.episode_id WHERE e.has_file = 1 GROUP BY 1;", showQuality, cancellationToken);
            await GroupAsync(connection, "SELECT COALESCE(video_codec, ''), COUNT(*) FROM series_wanted_state WHERE has_file = 1 GROUP BY 1;", showCodec, cancellationToken);
            await GroupAsync(connection, "SELECT w.library_id, COUNT(*) FROM episode_wanted_state w JOIN episode_entries e ON e.id = w.episode_id WHERE e.has_file = 1 GROUP BY w.library_id;", filesByLibrary, cancellationToken);
        }

        return new LibraryMixCounts(movieQuality, movieCodec, showQuality, showCodec, filesByLibrary);
    }

    private static async Task GroupAsync(DbConnection connection, string sql, Dictionary<string, int> into, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            into[key] = into.GetValueOrDefault(key) + reader.GetInt32(1);
        }
    }

    // ----------------------------------------------------------- collections

    /// <summary>Followed film collections, from the stored collection and member rows.</summary>
    public async Task<CollectionCounts> GetCollectionCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        int followed = 0, monitored = 0, syncFailing = 0;
        string? lastSynced = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*),
                       COALESCE(SUM(monitored), 0),
                       COALESCE(SUM(CASE WHEN last_sync_error IS NOT NULL AND last_sync_error <> '' THEN 1 ELSE 0 END), 0),
                       MAX(last_synced_utc)
                FROM movie_collections;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                followed = reader.GetInt32(0);
                monitored = reader.GetInt32(1);
                syncFailing = reader.GetInt32(2);
                lastSynced = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        int members = 0, inLibrary = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN local_movie_id IS NOT NULL THEN 1 ELSE 0 END), 0)
                FROM movie_collection_members WHERE is_excluded = 0;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                members = reader.GetInt32(0);
                inLibrary = reader.GetInt32(1);
            }
        }

        return new CollectionCounts(
            followed,
            monitored,
            members,
            inLibrary,
            syncFailing,
            DateTimeOffset.TryParse(lastSynced, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null);
    }

    /// <summary>Titles a list first brought in since <paramref name="sinceUtc"/>.</summary>
    public async Task<int> CountListTitlesAddedAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM intake_title_origins WHERE first_seen_utc >= @since;";
        Add(command, "@since", Iso(sinceUtc));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

public sealed record LibraryMixCounts(
    IReadOnlyDictionary<string, int> MovieQuality,
    IReadOnlyDictionary<string, int> MovieCodec,
    IReadOnlyDictionary<string, int> EpisodeQuality,
    IReadOnlyDictionary<string, int> ShowCodec,
    IReadOnlyDictionary<string, int> FilesByLibrary);

public sealed record CollectionCounts(int Followed, int Monitored, int Members, int InLibrary, int SyncFailing, DateTimeOffset? LastSyncedUtc);
