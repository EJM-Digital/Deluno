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

public static class DownloadClientTelemetryProfiles
{
    public static DownloadClientTelemetryCapabilities ResolveCapabilities(string protocol)
        => protocol.Trim().ToLowerInvariant() switch
        {
            "qbittorrent" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: true,
                SupportsImportPath: true,
                AuthMode: "form"),
            "sabnzbd" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: false,
                SupportsImportPath: true,
                AuthMode: "api-key"),
            "nzbget" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: false,
                SupportsImportPath: true,
                AuthMode: "basic"),
            "transmission" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: true,
                SupportsImportPath: true,
                AuthMode: "basic"),
            "deluge" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: true,
                SupportsImportPath: true,
                AuthMode: "password"),
            "utorrent" => new(
                SupportsQueue: true,
                SupportsHistory: true,
                SupportsPauseResume: true,
                SupportsRemove: true,
                SupportsRecheck: true,
                SupportsImportPath: false,
                AuthMode: "basic-token"),
            _ => new(
                SupportsQueue: false,
                SupportsHistory: false,
                SupportsPauseResume: false,
                SupportsRemove: false,
                SupportsRecheck: false,
                SupportsImportPath: false,
                AuthMode: "unknown")
        };

    public static string NormalizeStatus(
        string protocol,
        string? nativeStatus,
        double? progress,
        int? errorCode = null,
        string? errorMessage = null)
        => protocol.Trim().ToLowerInvariant() switch
        {
            "qbittorrent" => MapQbittorrentStatus(nativeStatus, progress),
            "sabnzbd" => MapSabnzbdStatus(nativeStatus, progress ?? 0),
            "transmission" => MapTransmissionStatus(nativeStatus, progress, errorCode, errorMessage),
            "deluge" or "nzbget" or "utorrent" => MapTextStatus(nativeStatus, progress),
            _ => MapTextStatus(nativeStatus, progress)
        };

    private static string MapQbittorrentStatus(string? state, double? progress)
    {
        var normalized = state?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 1 || normalized.Contains("upload")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("error") || normalized.Contains("stalled")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    private static string MapSabnzbdStatus(string? status, double progress)
    {
        var normalized = status?.ToLowerInvariant() ?? string.Empty;
        if (progress >= 99.9 || normalized.Contains("complete")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("fail") || normalized.Contains("error")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    private static string MapTransmissionStatus(string? status, double? progress, int? error, string? errorString)
    {
        if (error is > 0 || !string.IsNullOrWhiteSpace(errorString)) return DownloadQueueStatuses.Stalled;
        if ((progress ?? 0) >= 1) return DownloadQueueStatuses.ImportReady;
        return int.TryParse(status, out var numericStatus) && numericStatus == 4
            ? DownloadQueueStatuses.Downloading
            : DownloadQueueStatuses.Queued;
    }

    private static string MapTextStatus(string? status, double? progress)
    {
        var normalized = status?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 99.9 || normalized.Contains("complete") || normalized.Contains("seeding")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queue")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("error") || normalized.Contains("fail") || normalized.Contains("stalled")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }
}

public sealed record DownloadTelemetrySummary(
    int ActiveCount,
    int QueuedCount,
    int CompletedCount,
    int StalledCount,
    int ProcessingCount,
    int ImportReadyCount,
    double TotalSpeedMbps);

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
    string? SourcePath = null);

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
    DateTimeOffset CapturedUtc);

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
