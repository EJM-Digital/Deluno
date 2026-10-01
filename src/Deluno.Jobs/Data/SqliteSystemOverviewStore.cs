using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteSystemOverviewStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : ISystemOverviewStore
{
    public async Task RecordDriveSpaceAsync(string volumeKey, DateOnly day, long freeBytes, long totalBytes, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO drive_space_daily (volume_key, day, free_bytes, total_bytes, recorded_utc)
            VALUES (@key, @day, @free, @total, @recorded)
            ON CONFLICT(volume_key, day) DO UPDATE SET
                free_bytes = excluded.free_bytes,
                total_bytes = excluded.total_bytes,
                recorded_utc = excluded.recorded_utc;
            """;
        AddParameter(command, "@key", volumeKey);
        AddParameter(command, "@day", FormatDay(day));
        AddParameter(command, "@free", freeBytes);
        AddParameter(command, "@total", totalBytes);
        AddParameter(command, "@recorded", Format(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DriveSpaceDay>> ListDriveSpaceAsync(DateOnly since, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT volume_key, day, free_bytes, total_bytes
            FROM drive_space_daily
            WHERE day >= @since
            ORDER BY volume_key, day;
            """;
        AddParameter(command, "@since", FormatDay(since));

        var rows = new List<DriveSpaceDay>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DriveSpaceDay(
                reader.GetString(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    public async Task RecordStartAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO instance_starts (started_utc) VALUES (@started);";
        AddParameter(command, "@started", Format(startedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountStartsSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM instance_starts WHERE started_utc >= @since;";
        AddParameter(command, "@since", Format(sinceUtc));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM drive_space_daily WHERE day < @day;
            DELETE FROM instance_starts WHERE started_utc < @utc;
            """;
        AddParameter(command, "@day", FormatDay(DateOnly.FromDateTime(olderThanUtc.UtcDateTime)));
        AddParameter(command, "@utc", Format(olderThanUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobDayStats> GetJobDayStatsAsync(DateTimeOffset sinceUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM job_queue WHERE status IN ('completed', 'dead-letter') AND completed_utc >= @since)
                    + (SELECT COUNT(*) FROM job_queue WHERE status = 'running'),
                (SELECT COUNT(*) FROM job_queue WHERE status = 'dead-letter' AND completed_utc >= @since)
                    + (SELECT COUNT(*) FROM job_queue WHERE status = 'failed'),
                (SELECT MIN(scheduled_utc) FROM job_queue WHERE status = 'queued' AND scheduled_utc <= @now);
            """;
        AddParameter(command, "@since", Format(sinceUtc));
        AddParameter(command, "@now", Format(nowUtc));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new JobDayStats(0, 0, null);
        }

        double? oldest = null;
        if (!reader.IsDBNull(2)
            && DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var scheduled))
        {
            oldest = Math.Max(0, Math.Round((nowUtc - scheduled).TotalSeconds, 1));
        }

        return new JobDayStats(reader.GetInt32(0), reader.GetInt32(1), oldest);
    }

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
