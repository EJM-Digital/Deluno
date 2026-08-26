using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteDownloadThroughputRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory)
    : IDownloadThroughputRepository
{
    public async Task RecordSampleAsync(DownloadThroughputSample sample, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Upsert on the instant: a restart that re-samples the same second
        // replaces the reading rather than putting two points on one x.
        command.CommandText =
            """
            INSERT INTO download_throughput_samples (captured_utc, speed_mbps, active_count, upload_mbps)
            VALUES (@capturedUtc, @speedMbps, @activeCount, @uploadMbps)
            ON CONFLICT(captured_utc) DO UPDATE SET
                speed_mbps = excluded.speed_mbps,
                active_count = excluded.active_count,
                upload_mbps = excluded.upload_mbps;
            """;

        AddParameter(command, "@capturedUtc", Format(sample.CapturedUtc));
        AddParameter(command, "@speedMbps", sample.SpeedMbps);
        AddParameter(command, "@activeCount", sample.ActiveCount);
        AddParameter(command, "@uploadMbps", sample.UploadMbps);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadThroughputSample>> ListSamplesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT captured_utc, speed_mbps, active_count, upload_mbps
            FROM download_throughput_samples
            WHERE captured_utc >= @sinceUtc
            ORDER BY captured_utc ASC;
            """;

        AddParameter(command, "@sinceUtc", Format(sinceUtc));

        var samples = new List<DownloadThroughputSample>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new DownloadThroughputSample(
                CapturedUtc: DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                SpeedMbps: reader.GetDouble(1),
                ActiveCount: reader.GetInt32(2),
                UploadMbps: reader.IsDBNull(3) ? 0 : reader.GetDouble(3)));
        }

        return samples;
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_throughput_samples WHERE captured_utc < @cutoff;";
        AddParameter(command, "@cutoff", Format(olderThanUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Round-trippable and, crucially, lexicographically sortable — the queries compare these as text.</summary>
    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
