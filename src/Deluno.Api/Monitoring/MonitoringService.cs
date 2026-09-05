using System.Data.Common;
using System.Globalization;
using Deluno.Api.Health;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Connections.Data;
using Deluno.Libraries.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Monitoring;

public sealed class MonitoringService(
    IDelunoReadinessService readinessService,
    IDispatchMetricsRepository dispatchMetricsRepository,
    IJobQueueRepository jobQueueRepository,
    IMachineTelemetryRepository machineTelemetryRepository,
    IConnectionsRepository connectionsRepository,
    ILibrariesRepository librariesRepository,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    IApiLatencyTracker latencyTracker,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<MonitoringService> logger)
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
        var machine = await ReadMachineSampleAsync(cancellationToken);

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
            Alerts: alerts,
            Machine: machine);
    }

    /// <summary>
    /// The newest machine reading, or null. A dashboard cell that is absent
    /// says "not measured"; one showing zero would say "idle", which is a
    /// different and possibly false claim.
    /// </summary>
    private async Task<MachineTelemetrySample?> ReadMachineSampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await machineTelemetryRepository.GetLatestAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Machine telemetry was unavailable for the monitoring snapshot.");
            return null;
        }
    }

    public async Task<IReadOnlyList<MonitoringAlertItem>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var readiness = await readinessService.CheckAsync(cancellationToken);
        var dispatch = await dispatchMetricsRepository.GetMetricsAsync(cancellationToken);
        var storage = ReadStorageSummary(storageOptions.Value.DataRoot);
        return await BuildAlertsAsync(now, readiness, storage, dispatch, cancellationToken);
    }

    public async Task<Page<MonitoringDiagnosticItem>> SearchDiagnosticsAsync(
        MonitoringDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = query.Page.BoundedPageSize;
        var token = DelunoPageToken.Decode(query.Page.PageToken, 2);
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
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
              AND (@createdUtc IS NULL OR created_utc < @createdUtc OR (created_utc = @createdUtc AND id < @id))
              AND (
                    @query IS NULL OR
                    category LIKE @likeQuery OR
                    message LIKE @likeQuery OR
                    COALESCE(details_json, '') LIKE @likeQuery
                  )
            ORDER BY created_utc DESC, id DESC
            LIMIT @take;
            """;

        AddParameter(command, "@category", string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim());
        AddParameter(command, "@sinceUtc", query.SinceUtc?.ToString("O"));
        AddParameter(command, "@query", string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim());
        AddParameter(command, "@likeQuery", string.IsNullOrWhiteSpace(query.Query) ? null : $"%{query.Query.Trim()}%");
        AddParameter(command, "@take", pageSize + 1);
        AddParameter(command, "@createdUtc", token?[0]);
        AddParameter(command, "@id", token?[1]);

        var severityFilter = string.IsNullOrWhiteSpace(query.Severity)
            ? null
            : query.Severity.Trim();

        var results = new List<MonitoringDiagnosticItem>(pageSize + 1);
        var fetched = 0;
        string? cursorCreatedUtc = null;
        string? cursorId = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fetched++;
            if (fetched > pageSize)
            {
                continue;
            }

            var category = reader.GetString(1);
            var severity = SeverityForCategory(category);
            cursorCreatedUtc = reader.GetString(6);
            cursorId = reader.GetString(0);
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

        var hasMore = fetched > pageSize;

        return Page<MonitoringDiagnosticItem>.Of(
            results,
            hasMore ? DelunoPageToken.Encode(cursorCreatedUtc, cursorId) : null);
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
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
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
        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        // Counted in the database, not within a page of it. These used to be
        // counted inside ListAsync's newest 200 rows, so every number here
        // saturated at 200: on the lab rig the dashboard reported 136 failed
        // jobs against a queue holding 455, and would have reported the same
        // 200-ish figure against ten thousand.
        var jobCounts = await jobQueueRepository.CountJobsByStatusAsync(cancellationToken);
        int JobsWith(params string[] statuses) => statuses.Sum(status => jobCounts.GetValueOrDefault(status, 0));

        var activeJobs = JobsWith("running");
        var queuedJobs = JobsWith("queued");
        var failedJobs = JobsWith("failed", "dead-letter");

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
        var storageThresholdPercent = Math.Clamp(configuration.GetValue("Deluno:Monitoring:LowStorageThresholdPercent", 12d), 1d, 95d);
        var failureRateThresholdPercent = Math.Clamp(configuration.GetValue("Deluno:Monitoring:DispatchFailureRatePercent", 25d), 1d, 100d);
        // Keep a minimum sample floor because a failure-rate alert from a handful of dispatches is noisy.
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

        // A library Deluno cannot reach is not a library of deleted files, and
        // it is not nothing either. Every title in it is unverifiable, every
        // import into it will fail identically, and until this alert existed
        // the only symptom was silence. DESIGN-007 decision 12.
        foreach (var library in await librariesRepository.ListLibrariesAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(library.RootPath) || Directory.Exists(library.RootPath))
            {
                continue;
            }

            alerts.Add(new MonitoringAlertItem(
                Code: "library.unreachable",
                Severity: "critical",
                Summary: $"{library.Name} is not reachable.",
                Details: $"Nothing is at {library.RootPath}. Deluno has changed nothing about the titles in it and will resume once the path is back — check the drive or share is mounted.",
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
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
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

    private MonitoringStorageSummary ReadStorageSummary(string dataRoot)
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
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read storage information for {DataRoot}.", dataRoot);
            return new MonitoringStorageSummary(
                DataRoot: dataRoot,
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
