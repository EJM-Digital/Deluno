using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteMachineTelemetryRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory)
    : IMachineTelemetryRepository
{
    private const string Columns =
        "captured_utc, cpu_percent, memory_bytes, total_memory_bytes, " +
        "process_read_bytes_per_second, process_write_bytes_per_second, " +
        "disk_busy_percent, disk_read_bytes_per_second, disk_write_bytes_per_second";

    public async Task RecordSampleAsync(MachineTelemetrySample sample, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Upsert on the instant, so a restart that re-samples the same second
        // replaces the reading rather than putting two points on one x.
        command.CommandText =
            $"""
            INSERT INTO machine_telemetry_samples ({Columns})
            VALUES (@capturedUtc, @cpuPercent, @memoryBytes, @totalMemoryBytes,
                    @processRead, @processWrite, @diskBusy, @diskRead, @diskWrite)
            ON CONFLICT(captured_utc) DO UPDATE SET
                cpu_percent = excluded.cpu_percent,
                memory_bytes = excluded.memory_bytes,
                total_memory_bytes = excluded.total_memory_bytes,
                process_read_bytes_per_second = excluded.process_read_bytes_per_second,
                process_write_bytes_per_second = excluded.process_write_bytes_per_second,
                disk_busy_percent = excluded.disk_busy_percent,
                disk_read_bytes_per_second = excluded.disk_read_bytes_per_second,
                disk_write_bytes_per_second = excluded.disk_write_bytes_per_second;
            """;

        AddParameter(command, "@capturedUtc", Format(sample.CapturedUtc));
        AddParameter(command, "@cpuPercent", sample.CpuPercent);
        AddParameter(command, "@memoryBytes", sample.MemoryBytes);
        AddParameter(command, "@totalMemoryBytes", sample.TotalMemoryBytes);
        AddParameter(command, "@processRead", sample.ProcessReadBytesPerSecond);
        AddParameter(command, "@processWrite", sample.ProcessWriteBytesPerSecond);
        AddParameter(command, "@diskBusy", sample.DiskBusyPercent);
        AddParameter(command, "@diskRead", sample.DiskReadBytesPerSecond);
        AddParameter(command, "@diskWrite", sample.DiskWriteBytesPerSecond);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MachineTelemetrySample>> ListSamplesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM machine_telemetry_samples
            WHERE captured_utc >= @sinceUtc
            ORDER BY captured_utc ASC;
            """;

        AddParameter(command, "@sinceUtc", Format(sinceUtc));

        var samples = new List<MachineTelemetrySample>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(ReadSample(reader));
        }

        return samples;
    }

    public async Task<MachineTelemetrySample?> GetLatestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM machine_telemetry_samples
            ORDER BY captured_utc DESC
            LIMIT 1;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSample(reader) : null;
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM machine_telemetry_samples WHERE captured_utc < @cutoff;";
        AddParameter(command, "@cutoff", Format(olderThanUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MachineTelemetrySample ReadSample(DbDataReader reader)
        => new(
            CapturedUtc: DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CpuPercent: reader.GetDouble(1),
            MemoryBytes: reader.GetInt64(2),
            TotalMemoryBytes: reader.IsDBNull(3) ? null : reader.GetInt64(3),
            ProcessReadBytesPerSecond: reader.GetInt64(4),
            ProcessWriteBytesPerSecond: reader.GetInt64(5),
            DiskBusyPercent: reader.IsDBNull(6) ? null : reader.GetDouble(6),
            DiskReadBytesPerSecond: reader.IsDBNull(7) ? null : reader.GetInt64(7),
            DiskWriteBytesPerSecond: reader.IsDBNull(8) ? null : reader.GetInt64(8));

    /// <summary>Round-trippable and, crucially, lexicographically sortable — the queries compare these as text.</summary>
    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
