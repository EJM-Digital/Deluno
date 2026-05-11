using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class SqliteDispatchMetricsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDispatchMetricsRepository
{
    public async Task<DispatchMetrics> GetMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var now = timeProvider.GetUtcNow();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*) as total_dispatches,
                SUM(CASE WHEN grab_status = 'succeeded' THEN 1 ELSE 0 END) as successful_grabs,
                SUM(CASE WHEN grab_status = 'failed' THEN 1 ELSE 0 END) as failed_grabs,
                SUM(CASE WHEN detected_utc IS NOT NULL THEN 1 ELSE 0 END) as detected_downloads,
                SUM(CASE WHEN import_status = 'completed' THEN 1 ELSE 0 END) as successful_imports,
                SUM(CASE WHEN import_status = 'failed' THEN 1 ELSE 0 END) as failed_imports,
                SUM(CASE WHEN grab_status IS NULL OR import_status IS NULL OR (import_status = 'pending') THEN 1 ELSE 0 END) as active_dispatches
            FROM download_dispatches
            WHERE status != 'archived'
            """;

        var totalDispatches = 0L;
        var successfulGrabs = 0L;
        var failedGrabs = 0L;
        var detectedDownloads = 0L;
        var successfulImports = 0L;
        var failedImports = 0L;
        var activeDispatches = 0L;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            totalDispatches = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            successfulGrabs = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            failedGrabs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            detectedDownloads = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            successfulImports = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            failedImports = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            activeDispatches = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
        }

        var openAlertCount = await GetOpenAlertCountAsync(connection, cancellationToken);
        var averageGrabToDetectionMinutes = await GetAverageDurationAsync(
            connection,
            "AVG(CAST((julianday(detected_utc) - julianday(grab_attempted_utc)) * 24 * 60 AS INTEGER))",
            "WHERE grab_status = 'succeeded' AND detected_utc IS NOT NULL",
            cancellationToken);

        var averageDetectionToImportMinutes = await GetAverageDurationAsync(
            connection,
            "AVG(CAST((julianday(import_completed_utc) - julianday(detected_utc)) * 24 * 60 AS INTEGER))",
            "WHERE detected_utc IS NOT NULL AND import_completed_utc IS NOT NULL",
            cancellationToken);

        var grabFailuresByClient = await GetFailuresByClientAsync(connection, cancellationToken);
        var importFailuresByKind = await GetFailuresByKindAsync(connection, cancellationToken);

        return new DispatchMetrics(
            TotalDispatchesRecorded: totalDispatches,
            SuccessfulGrabs: successfulGrabs,
            FailedGrabs: failedGrabs,
            DetectedDownloads: detectedDownloads,
            SuccessfulImports: successfulImports,
            FailedImports: failedImports,
            ActiveDispatchesCount: (int)activeDispatches,
            RecoveryCasesOpenCount: openAlertCount,
            AverageGrabToDetection: TimeSpan.FromMinutes(averageGrabToDetectionMinutes ?? 0),
            AverageDetectionToImport: TimeSpan.FromMinutes(averageDetectionToImportMinutes ?? 0),
            GrabFailuresByClient: grabFailuresByClient,
            ImportFailuresByKind: importFailuresByKind,
            ComputedUtc: now);
    }

    public async Task RecordDispatchOutcomeAsync(
        string dispatchId,
        string mediaType,
        string? grabStatus,
        string? importStatus,
        DateTimeOffset? grabAttemptedUtc,
        DateTimeOffset? detectedUtc,
        DateTimeOffset? importCompletedUtc,
        CancellationToken cancellationToken)
    {
        // Metrics are computed on-demand from dispatch records.
        // This method is a no-op for SQLite-based metrics.
        await Task.CompletedTask;
    }

    private static async Task<int> GetOpenAlertCountAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dispatch_alerts WHERE acknowledged = 0";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    private static async Task<int?> GetAverageDurationAsync(
        DbConnection connection,
        string aggregateExpr,
        string whereClause,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {aggregateExpr}
            FROM download_dispatches
            {whereClause}
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is DBNull or null)
            return null;
        if (result is long longVal)
            return (int)longVal;
        if (result is int intVal)
            return intVal;

        return null;
    }

    private static async Task<IReadOnlyDictionary<string, int>> GetFailuresByClientAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var failures = new Dictionary<string, int>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT download_client_name, COUNT(*) as failure_count
            FROM download_dispatches
            WHERE grab_status = 'failed'
            GROUP BY download_client_name
            ORDER BY failure_count DESC
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clientName = reader.GetString(0);
            var count = (int)reader.GetInt64(1);
            failures[clientName] = count;
        }

        return failures;
    }

    private static async Task<IReadOnlyDictionary<string, int>> GetFailuresByKindAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var failures = new Dictionary<string, int>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT import_failure_code, COUNT(*) as failure_count
            FROM download_dispatches
            WHERE import_status = 'failed' AND import_failure_code IS NOT NULL
            GROUP BY import_failure_code
            ORDER BY failure_count DESC
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var failureKind = reader.GetString(0);
            var count = (int)reader.GetInt64(1);
            failures[failureKind] = count;
        }

        return failures;
    }
}
