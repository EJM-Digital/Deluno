using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Health;

public sealed class DelunoReadinessService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider)
    : IDelunoReadinessService
{
    private static readonly string[] RequiredDatabases =
    [
        DelunoDatabaseNames.Platform,
        DelunoDatabaseNames.Movies,
        DelunoDatabaseNames.Series,
        DelunoDatabaseNames.Jobs,
        DelunoDatabaseNames.Cache
    ];

    public static DelunoLivenessResponse Live()
        => new("live", DateTimeOffset.UtcNow);

    public async Task<DelunoReadinessResponse> CheckAsync(CancellationToken cancellationToken)
    {
        var checkedUtc = timeProvider.GetUtcNow();
        var checks = new List<ReadinessCheckResult>();

        foreach (var databaseName in RequiredDatabases)
        {
            checks.Add(await CheckDatabaseAsync(databaseName, cancellationToken));
        }

        checks.Add(await CheckStorageWritableAsync(cancellationToken));
        checks.Add(await CheckWorkerHeartbeatAsync(checkedUtc, cancellationToken));
        checks.Add(await CheckQueuePressureAsync(checkedUtc, cancellationToken));

        var ready = checks.All(check => string.Equals(check.Status, "ready", StringComparison.OrdinalIgnoreCase));
        return new DelunoReadinessResponse(
            Ready: ready,
            Status: ready ? "ready" : "not_ready",
            CheckedUtc: checkedUtc,
            Checks: checks);
    }

    private async Task<ReadinessCheckResult> CheckDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(databaseName, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);

            return Ready(
                $"database:{databaseName}",
                $"{databaseName}.db is reachable.",
                new Dictionary<string, object?>
                {
                    ["path"] = databaseConnectionFactory.GetDatabasePath(databaseName)
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotReady(
                $"database:{databaseName}",
                $"{databaseName}.db is not reachable: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["path"] = databaseConnectionFactory.GetDatabasePath(databaseName)
                });
        }
    }

    private async Task<ReadinessCheckResult> CheckStorageWritableAsync(CancellationToken cancellationToken)
    {
        var dataRoot = storageOptions.Value.DataRoot;
        try
        {
            Directory.CreateDirectory(dataRoot);
            var probePath = Path.Combine(dataRoot, $".deluno-readiness-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probePath, "ready", cancellationToken);
            File.Delete(probePath);

            return Ready(
                "storage:writable",
                "Storage root exists and accepts writes.",
                new Dictionary<string, object?> { ["path"] = dataRoot });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotReady(
                "storage:writable",
                $"Storage root is not writable: {ex.Message}",
                new Dictionary<string, object?> { ["path"] = dataRoot });
        }
    }

    private async Task<ReadinessCheckResult> CheckWorkerHeartbeatAsync(
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
            var lastSeen = await ReadScalarAsync<string?>(
                connection,
                "SELECT MAX(last_seen_utc) FROM worker_heartbeats;",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(lastSeen))
            {
                return NotReady(
                    "worker:heartbeat",
                    "No worker heartbeat has been recorded.",
                    new Dictionary<string, object?>());
            }

            var lastSeenUtc = DateTimeOffset.Parse(lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var age = checkedUtc - lastSeenUtc;
            var status = age <= TimeSpan.FromSeconds(45);
            var details = new Dictionary<string, object?>
            {
                ["lastSeenUtc"] = lastSeenUtc,
                ["ageSeconds"] = Math.Round(age.TotalSeconds, 1)
            };

            return status
                ? Ready("worker:heartbeat", "Worker heartbeat is fresh.", details)
                : NotReady("worker:heartbeat", "Worker heartbeat is stale.", details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotReady(
                "worker:heartbeat",
                $"Worker heartbeat could not be checked: {ex.Message}",
                new Dictionary<string, object?>());
        }
    }

    private async Task<ReadinessCheckResult> CheckQueuePressureAsync(
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
            var stalledRunning = await ReadScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM job_queue
                WHERE status = 'running'
                  AND leased_until_utc IS NOT NULL
                  AND leased_until_utc < @now;
                """,
                cancellationToken,
                ("@now", checkedUtc.ToString("O")));
            var laggedQueued = await ReadScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM job_queue
                WHERE status = 'queued'
                  AND scheduled_utc <= @lagThreshold;
                """,
                cancellationToken,
                ("@lagThreshold", checkedUtc.AddMinutes(-15).ToString("O")));

            var details = new Dictionary<string, object?>
            {
                ["stalledRunning"] = stalledRunning,
                ["laggedQueued"] = laggedQueued,
                ["lagThresholdMinutes"] = 15
            };

            return stalledRunning == 0 && laggedQueued == 0
                ? Ready("jobs:queue", "No stalled or lagged jobs detected.", details)
                : NotReady("jobs:queue", "Queue contains stalled or lagged jobs.", details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotReady(
                "jobs:queue",
                $"Queue state could not be checked: {ex.Message}",
                new Dictionary<string, object?>());
        }
    }

    private static async Task<T?> ReadScalarAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    private static ReadinessCheckResult Ready(
        string name,
        string message,
        IReadOnlyDictionary<string, object?> details)
        => new(name, "ready", message, details);

    private static ReadinessCheckResult NotReady(
        string name,
        string message,
        IReadOnlyDictionary<string, object?> details)
        => new(name, "not_ready", message, details);
}
