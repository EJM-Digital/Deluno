namespace Deluno.Integrations.DownloadClients;

public sealed record DownloadTelemetrySummary(
    int ActiveCount,
    int QueuedCount,
    int CompletedCount,
    int StalledCount,
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

public sealed record DownloadClientTelemetrySnapshot(
    string ClientId,
    string ClientName,
    string Protocol,
    string? EndpointUrl,
    string HealthStatus,
    string? LastHealthMessage,
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
