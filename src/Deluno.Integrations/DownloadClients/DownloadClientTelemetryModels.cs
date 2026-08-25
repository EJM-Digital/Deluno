namespace Deluno.Integrations.DownloadClients;

public static class DownloadQueueStatuses
{
    public const string Downloading = "downloading";
    public const string Queued = "queued";
    public const string ImportReady = "importReady";
    public const string Stalled = "stalled";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string ProcessingFailed = "processingFailed";
    public const string WaitingForProcessor = "waitingForProcessor";
    public const string ImportQueued = "importQueued";
    public const string Imported = "imported";
    public const string ImportFailed = "importFailed";
    public const string Completed = "completed";
}

internal static class DownloadClientTelemetryLimits
{
    // The history drawer is a recent-activity view, not an archive. Dispatch
    // history in jobs.db is the archive, so one explicit window protects every
    // client adapter without silently varying by protocol.
    public const int HistoryWindow = 200;
}

public sealed record DownloadTelemetrySummary(
    int ActiveCount,
    int QueuedCount,
    int CompletedCount,
    int StalledCount,
    /// <summary>
    /// Everything past the download and before the library: items handed to a
    /// post-processor and items whose import job is queued or running. The two
    /// are different problems when they stick, so
    /// <see cref="WaitingForProcessorCount"/> reports the processor share
    /// separately rather than leaving callers to label the whole bucket.
    /// </summary>
    int ProcessingCount,
    int ImportReadyCount,
    double TotalSpeedMbps,
    /// <summary>
    /// Downloads finished but held back because their library refines before
    /// importing and the cleaned output has not arrived. A subset of
    /// <see cref="ProcessingCount"/>; the remainder is import work.
    /// </summary>
    int WaitingForProcessorCount = 0);

public sealed record DownloadQueueItem(
    string Id,
    string ClientId,
    string ClientName,
    string Protocol,
    string MediaType,
    string Title,
    string ReleaseName,
    string Category,
    string Status,
    double Progress,
    double SpeedMbps,
    int EtaSeconds,
    long SizeBytes,
    long DownloadedBytes,
    int Peers,
    string IndexerName,
    string? ErrorMessage,
    DateTimeOffset AddedUtc,
    string? SourcePath = null,
    string? LibraryId = null,
    IReadOnlyList<DownloadHealthFinding>? HealthFindings = null);

/// <summary>
/// An observational queue-health signal. These findings never cause Deluno to remove
/// download data; they explain why an item needs attention and which reversible action
/// a person can consider first.
/// </summary>
public sealed record DownloadHealthFinding(
    string Severity,
    string Kind,
    string Summary,
    string Evidence,
    string RecommendedAction,
    bool CanSafelyRetry,
    bool CanSafelyRemove,
    int StrikeCount = 0,
    bool CandidateBlocked = false,
    DateTimeOffset? IgnoredUntilUtc = null);

public sealed record DownloadClientHistoryItem(
    string Id,
    string ClientId,
    string ClientName,
    string Protocol,
    string MediaType,
    string Title,
    string ReleaseName,
    string Category,
    string Outcome,
    string IndexerName,
    long SizeBytes,
    DateTimeOffset CompletedUtc,
    string? ErrorMessage,
    string? SourcePath = null);

public sealed record DownloadClientTelemetryCapabilities(
    bool SupportsQueue,
    bool SupportsHistory,
    bool SupportsPauseResume,
    bool SupportsRemove,
    bool SupportsRecheck,
    bool SupportsImportPath,
    string AuthMode);

public sealed record DownloadClientTelemetrySnapshot(
    string ClientId,
    string ClientName,
    string Protocol,
    string? EndpointUrl,
    string HealthStatus,
    string? LastHealthMessage,
    DownloadClientTelemetryCapabilities Capabilities,
    DownloadTelemetrySummary Summary,
    IReadOnlyList<DownloadQueueItem> Queue,
    IReadOnlyList<DownloadClientHistoryItem> History,
    DateTimeOffset CapturedUtc,
    bool HistoryTruncated = false);

public sealed record DownloadTelemetryOverview(
    DownloadTelemetrySummary Summary,
    IReadOnlyList<DownloadClientTelemetrySnapshot> Clients,
    DateTimeOffset CapturedUtc);

public sealed record DownloadClientActionRequest(
    string Action,
    string QueueItemId);

public sealed record DownloadClientActionResult(
    string ClientId,
    string QueueItemId,
    string Action,
    bool Succeeded,
    string Message);

public sealed record DownloadHealthRemediationReport(
    int Evaluated,
    int ReplacementSearchesQueued,
    int ClientEntriesRemoved,
    int PayloadsPurged,
    int Skipped,
    IReadOnlyList<string> Notes);
