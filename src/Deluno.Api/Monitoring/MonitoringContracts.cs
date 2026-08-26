using Deluno.Contracts;

namespace Deluno.Api.Monitoring;

/// <param name="Machine">
/// How hard the machine is working (#272). Null before the sampler has run, or
/// where it cannot read — the dashboard simply says nothing rather than
/// claiming an idle machine. It is the newest stored reading rather than a
/// fresh probe: rates are measured between calls, so a second prober would
/// reset the sampler's baseline and both would report nonsense.
/// </param>
public sealed record MonitoringDashboardSnapshot(
    DateTimeOffset GeneratedUtc,
    MonitoringReadinessSummary Readiness,
    MonitoringStorageSummary Storage,
    MonitoringServiceSummary Services,
    MonitoringPerformanceSummary Performance,
    IReadOnlyList<MonitoringAlertItem> Alerts,
    MachineTelemetrySample? Machine = null);

public sealed record MonitoringReadinessSummary(
    string Status,
    bool Ready,
    int TotalChecks,
    int FailedChecks);

public sealed record MonitoringStorageSummary(
    string DataRoot,
    long? TotalBytes,
    long? FreeBytes,
    double? FreePercent,
    bool LowStorage);

public sealed record MonitoringServiceSummary(
    int IndexersHealthy,
    int IndexersTotal,
    int DownloadClientsHealthy,
    int DownloadClientsTotal,
    int ActiveJobs,
    int QueuedJobs,
    int FailedJobs,
    int OpenDispatchAlerts);

public sealed record MonitoringPerformanceSummary(
    int SearchCyclesSampled,
    double? AverageSearchCycleSeconds,
    double? AverageGrabToDetectionSeconds,
    double? AverageDetectionToImportSeconds,
    ApiLatencySnapshot ApiLatency);

public sealed record MonitoringAlertItem(
    string Code,
    string Severity,
    string Summary,
    string Details,
    DateTimeOffset DetectedUtc);

public sealed record MonitoringDiagnosticsQuery(
    string? Query,
    string? Category,
    string? Severity,
    DateTimeOffset? SinceUtc,
    PageRequest Page);

public sealed record MonitoringDiagnosticItem(
    string Id,
    string Category,
    string Severity,
    string Message,
    string? DetailsJson,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc);

public sealed record ApiLatencySnapshot(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int RequestCount,
    int ErrorCount,
    double ErrorRatePercent,
    double AverageMs,
    double P95Ms);

public sealed record MonitoringExportSnapshot(
    MonitoringDashboardSnapshot Dashboard,
    IReadOnlyDictionary<string, double> NumericMetrics);
