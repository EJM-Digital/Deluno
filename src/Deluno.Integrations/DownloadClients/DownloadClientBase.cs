using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients;

public abstract class DownloadClientBase : IDownloadClient
{
    public abstract string Protocol { get; }

    public abstract DownloadClientTelemetryCapabilities Capabilities { get; }

    public abstract Task<DownloadClientGrabResult> GrabAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken);

    public abstract Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken);

    public abstract Task<DownloadClientActionResult> ExecuteActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken);

    public virtual Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

    public virtual Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken)
        => Task.FromResult(new DownloadClientCategoryCheckResult(
            client.Id,
            client.Name,
            category,
            DownloadClientCategoryStatuses.Unsupported,
            $"Deluno cannot list categories for {client.Protocol} yet. You can still use the client default.",
            Supported: false,
            Found: false));

    public abstract string NormalizeStatus(
        string? nativeStatus,
        double? progress,
        int? errorCode = null,
        string? errorMessage = null);

    protected DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
    {
        var historyItems = (history ?? CreateHistoryFromQueue(client, queue, capturedUtc)).ToArray();
        return new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Capabilities: Capabilities,
            Summary: Summarize(queue),
            Queue: queue,
            History: historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            CapturedUtc: capturedUtc,
            HistoryTruncated: historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow);
    }

    protected static IReadOnlyList<DownloadClientHistoryItem> CreateHistoryFromQueue(
        DownloadClientItem client,
        IEnumerable<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc)
        => queue
            .Where(item => item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady || !string.IsNullOrWhiteSpace(item.ErrorMessage))
            .OrderByDescending(item => item.AddedUtc)
            .Select(item => new DownloadClientHistoryItem(
                item.Id, client.Id, client.Name, client.Protocol, item.MediaType, item.Title,
                item.ReleaseName, item.Category,
                !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? "failed"
                    : item.Status == DownloadQueueStatuses.ImportReady
                        ? DownloadQueueStatuses.ImportReady
                        : DownloadQueueStatuses.Completed,
                item.IndexerName, item.SizeBytes,
                item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady ? capturedUtc : item.AddedUtc,
                item.ErrorMessage, item.SourcePath))
            .ToArray();

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
    {
        var items = queue.ToArray();
        return new DownloadTelemetrySummary(
            items.Count(item => item.Status == DownloadQueueStatuses.Downloading),
            items.Count(item => item.Status == DownloadQueueStatuses.Queued),
            items.Count(item => item.Status == DownloadQueueStatuses.Completed),
            items.Count(item => item.Status == DownloadQueueStatuses.Stalled),
            items.Count(item => item.Status is DownloadQueueStatuses.Processing or DownloadQueueStatuses.Processed or DownloadQueueStatuses.ProcessingFailed or DownloadQueueStatuses.WaitingForProcessor or DownloadQueueStatuses.ImportQueued),
            items.Count(item => item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed),
            Math.Round(items.Sum(item => item.SpeedMbps), 1),
            items.Count(item => item.Status == DownloadQueueStatuses.WaitingForProcessor));
    }
}
