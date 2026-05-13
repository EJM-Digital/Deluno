using System.Data.Common;
using System.Globalization;
using Deluno.Api.Health;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Monitoring;

public sealed class MonitoringService(
    IDelunoReadinessService readinessService,
    IDispatchMetricsRepository dispatchMetricsRepository,
    IJobQueueRepository jobQueueRepository,
    IPlatformSettingsRepository platformSettingsRepository,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    IApiLatencyTracker latencyTracker,
    IConfiguration configuration,
    TimeProvider timeProvider)
    : IMonitoringService
{
    public async Task<MonitoringDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var readiness = await readinessService.CheckAsync(cancellationToken);
        var dispatch = await dispatchMetricsRepository.GetMetricsAsync(cancellationToken);
        var storage = ReadStorageSummary(storageOptions.Value.DataRoot);
        var services = await ReadServiceSummaryAsync(dispatch.RecoveryCasesOpenCount, cancellationToken);
        var performance = await ReadPerformanceSummaryAsync(dispatch, cancellationToken);
        var alerts = await BuildAlertsAsync(now, readiness, storage, dispatch, cancellationToken);

        return new MonitoringDashboardSnapshot(
            GeneratedUtc: now,
            Readiness: new MonitoringReadinessSummary(
                Status: readiness.Status,
                Ready: readiness.Ready,
                TotalChecks: readiness.Checks.Count,
                FailedChecks: readiness.Checks.Count(check => !string.Equals(check.Status, "ready", StringComparison.OrdinalIgnoreCase))),
            Storage: storage,
            Services: services,
            Performance: performance,
            Alerts: alerts);
    }

    public async Task<IReadOnlyList<MonitoringAlertItem>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var readiness = await readinessService.CheckAsync(cancellationToken);
        var dispatch = await dispatchMetricsRepository.GetMetricsAsync(cancellationToken);
        var storage = ReadStorageSummary(storageOptions.Value.DataRoot);
        return await BuildAlertsAsync(now, readiness, storage, dispatch, cancellationToken);
    }

    public async Task<IReadOnlyList<MonitoringDiagnosticItem>> SearchDiagnosticsAsync(
        MonitoringDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        var cappedTake = Math.Clamp(query.Take, 1, 500);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, category, message, details_json, related_entity_type, related_entity_id, created_utc
            FROM activity_events
            WHERE (@category IS NULL OR category = @category)
              AND (@sinceUtc IS NULL OR created_utc >= @sinceUtc)
              AND (
                    @query IS NULL OR
                    category LIKE @likeQuery OR
                    message LIKE @likeQuery OR
                    COALESCE(details_json, '') LIKE @likeQuery
                  )
            ORDER BY created_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@category", string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim());
        AddParameter(command, "@sinceUtc", query.SinceUtc?.ToString("O"));
        AddParameter(command, "@query", string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim());
        AddParameter(command, "@likeQuery", string.IsNullOrWhiteSpace(query.Query) ? null : $"%{query.Query.Trim()}%");
        AddParameter(command, "@take", cappedTake);

        var severityFilter = string.IsNullOrWhiteSpace(query.Severity)
            ? null
            : query.Severity.Trim();

        var results = new List<MonitoringDiagnosticItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var category = reader.GetString(1);
            var severity = SeverityForCategory(category);
            if (severityFilter is not null &&
                !string.Equals(severity, severityFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new MonitoringDiagnosticItem(
                Id: reader.GetString(0),
                Category: category,
                Severity: severity,
                Message: reader.GetString(2),
                DetailsJson: reader.IsDBNull(3) ? null : reader.GetString(3),
                RelatedEntityType: reader.IsDBNull(4) ? null : reader.GetString(4),
                RelatedEntityId: reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedUtc: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return results;
    }

    public async Task<MonitoringExportSnapshot> BuildExportSnapshotAsync(CancellationToken cancellationToken)
    {
        var dashboard = await GetDashboardAsync(cancellationToken);
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["deluno_monitoring_readiness_ready"] = dashboard.Readiness.Ready ? 1 : 0,
            ["deluno_monitoring_readiness_failed_checks"] = dashboard.Readiness.FailedChecks,
            ["deluno_monitoring_storage_low"] = dashboard.Storage.LowStorage ? 1 : 0,
            ["deluno_monitoring_alerts_open"] = dashboard.Alerts.Count,
            ["deluno_monitoring_indexers_healthy"] = dashboard.Services.IndexersHealthy,
            ["deluno_monitoring_indexers_total"] = dashboard.Services.IndexersTotal,
            ["deluno_monitoring_clients_healthy"] = dashboard.Services.DownloadClientsHealthy,
            ["deluno_monitoring_clients_total"] = dashboard.Services.DownloadClientsTotal,
            ["deluno_monitoring_jobs_active"] = dashboard.Services.ActiveJobs,
            ["deluno_monitoring_jobs_queued"] = dashboard.Services.QueuedJobs,
            ["deluno_monitoring_jobs_failed"] = dashboard.Services.FailedJobs,
            ["deluno_monitoring_dispatch_alerts_open"] = dashboard.Services.OpenDispatchAlerts,
            ["deluno_monitoring_api_requests"] = dashboard.Performance.ApiLatency.RequestCount,
            ["deluno_monitoring_api_errors"] = dashboard.Performance.ApiLatency.ErrorCount,
            ["deluno_monitoring_api_error_rate_percent"] = dashboard.Performance.ApiLatency.ErrorRatePercent,
            ["deluno_monitoring_api_latency_avg_ms"] = dashboard.Performance.ApiLatency.AverageMs,
            ["deluno_monitoring_api_latency_p95_ms"] = dashboard.Performance.ApiLatency.P95Ms,
            ["deluno_monitoring_search_cycles_sampled"] = dashboard.Performance.SearchCyclesSampled
        };

        if (dashboard.Storage.FreePercent is not null)
        {
            metrics["deluno_monitoring_storage_free_percent"] = dashboard.Storage.FreePercent.Value;
        }

        if (dashboard.Performance.AverageSearchCycleSeconds is not null)
        {
            metrics["deluno_monitoring_search_cycle_avg_seconds"] = dashboard.Performance.AverageSearchCycleSeconds.Value;
        }

        if (dashboard.Performance.AverageGrabToDetectionSeconds is not null)
        {
            metrics["deluno_monitoring_grab_to_detection_avg_seconds"] = dashboard.Performance.AverageGrabToDetectionSeconds.Value;
        }

        if (dashboard.Performance.AverageDetectionToImportSeconds is not null)
        {
            metrics["deluno_monitoring_detection_to_import_avg_seconds"] = dashboard.Performance.AverageDetectionToImportSeconds.Value;
        }

        return new MonitoringExportSnapshot(dashboard, metrics);
    }

    private async Task<MonitoringPerformanceSummary> ReadPerformanceSummaryAsync(
        DispatchMetrics dispatch,
        CancellationToken cancellationToken)
    {
        var (sampleCount, avgSeconds) = await QuerySearchCycleAverageAsync(
            timeProvider.GetUtcNow().AddHours(-24),
            cancellationToken);

        var apiLatency = latencyTracker.GetSnapshot(TimeSpan.FromMinutes(15));
        var grabToDetection = dispatch.AverageGrabToDetection.TotalSeconds <= 0
            ? (double?)null
            : Math.Round(dispatch.AverageGrabToDetection.TotalSeconds, 2);
        var detectionToImport = dispatch.AverageDetectionToImport.TotalSeconds <= 0
            ? (double?)null
            : Math.Round(dispatch.AverageDetectionToImport.TotalSeconds, 2);

        return new MonitoringPerformanceSummary(
            SearchCyclesSampled: sampleCount,
            AverageSearchCycleSeconds: avgSeconds,
            AverageGrabToDetectionSeconds: grabToDetection,
            AverageDetectionToImportSeconds: detectionToImport,
            ApiLatency: apiLatency);
    }

    private async Task<(int SampleCount, double? AverageSeconds)> QuerySearchCycleAverageAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*),
                AVG((julianday(completed_utc) - julianday(started_utc)) * 86400.0)
            FROM search_cycle_runs
            WHERE completed_utc IS NOT NULL
              AND started_utc >= @sinceUtc;
            """;
        AddParameter(command, "@sinceUtc", sinceUtc.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, null);
        }

        var count = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
        var avg = reader.IsDBNull(1) ? (double?)null : Math.Round(reader.GetDouble(1), 2);
        return (count, avg);
    }

    private async Task<MonitoringServiceSummary> ReadServiceSummaryAsync(
        int openDispatchAlerts,
        CancellationToken cancellationToken)
    {
        var indexers = await platformSettingsRepository.ListIndexersAsync(cancellationToken);
        var clients = await platformSettingsRepository.ListDownloadClientsAsync(cancellationToken);
        var jobs = await jobQueueRepository.ListAsync(200, cancellationToken);

        var activeJobs = jobs.Count(job => string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase));
        var queuedJobs = jobs.Count(job => string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase));
        var failedJobs = jobs.Count(job =>
            string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase));

        return new MonitoringServiceSummary(
            IndexersHealthy: indexers.Count(item => string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)),
            IndexersTotal: indexers.Count,
            DownloadClientsHealthy: clients.Count(item => string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)),
            DownloadClientsTotal: clients.Count,
            ActiveJobs: activeJobs,
            QueuedJobs: queuedJobs,
            FailedJobs: failedJobs,
            OpenDispatchAlerts: openDispatchAlerts);
    }

    private async Task<IReadOnlyList<MonitoringAlertItem>> BuildAlertsAsync(
        DateTimeOffset now,
        DelunoReadinessResponse readiness,
        MonitoringStorageSummary storage,
        DispatchMetrics dispatch,
        CancellationToken cancellationToken)
    {
        var alerts = new List<MonitoringAlertItem>();
        var storageThresholdPercent = Math.Clamp(configuration.GetValue("Deluno:Monitoring:LowStorageThresholdPercent", 12d), 1d, 40d);
        var failureRateThresholdPercent = Math.Clamp(configuration.GetValue("Deluno:Monitoring:DispatchFailureRatePercent", 25d), 1d, 90d);
        var minSampleForErrorRate = Math.Clamp(configuration.GetValue("Deluno:Monitoring:MinDispatchSampleForFailureAlert", 20), 5, 500);

        if (storage.FreePercent is not null && storage.FreePercent <= storageThresholdPercent)
        {
            alerts.Add(new MonitoringAlertItem(
                Code: "storage.low",
                Severity: "critical",
                Summary: "Storage is running low.",
                Details: $"Only {storage.FreePercent.Value.ToString("0.##", CultureInfo.InvariantCulture)}% free remains under {storage.DataRoot}.",
                DetectedUtc: now));
        }

        var failedChecks = readiness.Checks
            .Where(check => !string.Equals(check.Status, "ready", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (failedChecks.Length > 0)
        {
            alerts.Add(new MonitoringAlertItem(
                Code: "services.unhealthy",
                Severity: "critical",
                Summary: "One or more system health checks failed.",
                Details: string.Join(" | ", failedChecks.Select(check => $"{check.Name}: {check.Message}")),
                DetectedUtc: now));
        }

        if (dispatch.RecoveryCasesOpenCount > 0)
        {
            alerts.Add(new MonitoringAlertItem(
                Code: "dispatch.recovery-open",
                Severity: "warning",
                Summary: "Dispatch recovery alerts need attention.",
                Details: $"{dispatch.RecoveryCasesOpenCount} dispatch alert(s) are open.",
                DetectedUtc: now));
        }

        var dispatchRates = await QueryDispatchFailureRateAsync(timeProvider.GetUtcNow().AddHours(-24), cancellationToken);
        if (dispatchRates.TotalSamples >= minSampleForErrorRate &&
            dispatchRates.FailureRatePercent >= failureRateThresholdPercent)
        {
            alerts.Add(new MonitoringAlertItem(
                Code: "dispatch.failure-rate",
                Severity: "warning",
                Summary: "Dispatch failure rate exceeded threshold.",
                Details: $"Last 24h: {dispatchRates.FailedSamples}/{dispatchRates.TotalSamples} failed ({dispatchRates.FailureRatePercent.ToString("0.##", CultureInfo.InvariantCulture)}%).",
                DetectedUtc: now));
        }

        return alerts
            .OrderByDescending(alert => alert.Severity switch
            {
                "critical" => 3,
                "warning" => 2,
                "error" => 2,
                _ => 1
            })
            .ThenByDescending(alert => alert.DetectedUtc)
            .ToArray();
    }

    private async Task<(int TotalSamples, int FailedSamples, double FailureRatePercent)> QueryDispatchFailureRateAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*) AS total_count,
                SUM(CASE WHEN grab_status = 'failed' THEN 1 ELSE 0 END) AS failed_count
            FROM download_dispatches
            WHERE grab_attempted_utc IS NOT NULL
              AND grab_attempted_utc >= @sinceUtc
              AND status != 'archived';
            """;
        AddParameter(command, "@sinceUtc", sinceUtc.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0);
        }

        var total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
        var failed = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        var rate = total == 0 ? 0 : Math.Round((double)failed / total * 100, 2);
        return (total, failed, rate);
    }

    private static MonitoringStorageSummary ReadStorageSummary(string dataRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(dataRoot);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return new MonitoringStorageSummary(fullPath, null, null, null, false);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return new MonitoringStorageSummary(fullPath, null, null, null, false);
            }

            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var percent = total <= 0 ? (double?)null : Math.Round((double)free / total * 100d, 2);
            return new MonitoringStorageSummary(
                DataRoot: fullPath,
                TotalBytes: total,
                FreeBytes: free,
                FreePercent: percent,
                LowStorage: percent is not null && percent <= 12d);
        }
        catch
        {
            return new MonitoringStorageSummary(
                DataRoot: Path.GetFullPath(dataRoot),
                TotalBytes: null,
                FreeBytes: null,
                FreePercent: null,
                LowStorage: false);
        }
    }

    private static string SeverityForCategory(string category)
    {
        if (category.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("dead-letter", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (category.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("attention", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (category.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        return "info";
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
