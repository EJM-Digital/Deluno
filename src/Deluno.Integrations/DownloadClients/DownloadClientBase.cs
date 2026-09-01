using Deluno.Connections.Contracts;
using Deluno.Contracts;

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
        IReadOnlyList<DownloadClientHistoryItem>? history = null,
        IntegrationFailure? failure = null)
    {
        var normalizedQueue = (queue ?? [])
            .Select(DownloadClientHelpers.NormalizeQueueFailure)
            .ToArray();
        var historyItems = (history ?? CreateHistoryFromQueue(client, normalizedQueue, capturedUtc))
            .Select(DownloadClientHelpers.NormalizeHistoryFailure)
            .ToArray();
        return new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Capabilities: Capabilities,
            Summary: Summarize(normalizedQueue),
            Queue: normalizedQueue,
            History: historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            CapturedUtc: capturedUtc,
            HistoryTruncated: historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow)
        {
            LastFailure = failure
        };
    }

    protected DownloadClientTelemetrySnapshot CreateConfigurationSnapshot(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        string message)
        => CreateSnapshot(
            client,
            [],
            capturedUtc,
            "degraded",
            message,
            failure: IntegrationFailureFactory.FromLegacy(
                "download-client",
                client.Id,
                client.Name,
                "telemetry",
                "configuration",
                message));

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
                item.ErrorMessage,
                item.SourcePath,
                HistorySource: "queue-derived",
                ExternalId: item.Id,
                Failure: item.Failure))
            .ToArray();

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
        => DownloadQueueSummary.Of(queue);
}
