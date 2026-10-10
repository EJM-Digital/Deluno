using Deluno.Connections.Usenet;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

public abstract class DownloadClientBase : IDownloadClient
{
    public abstract string Protocol { get; }

    public abstract DownloadClientTelemetryCapabilities Capabilities { get; }

    // Declared here, not left to the interface's default: a client class that
    // inherits the interface through this base could otherwise declare its own
    // property and never be asked (#1073).
    public virtual bool SetsSaveFolderPerGrab => false;

    public virtual Task<bool?> HasUsenetProviderAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult<bool?>(null);

    public virtual Task<UsenetProviderClientResult?> SetUsenetProviderAsync(DownloadClientItem client, UsenetProviderRequest request, CancellationToken cancellationToken)
        => Task.FromResult<UsenetProviderClientResult?>(null);

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

    /// <summary>#1240: a client that can move a torrent to another category or label overrides this; Usenet clients cannot.</summary>
    public virtual Task<DownloadClientActionResult> SetCategoryAsync(
        DownloadClientItem client,
        string queueItemId,
        string category,
        string? currentCategory,
        CancellationToken cancellationToken)
        => Task.FromResult(DownloadClientHelpers.Unsupported(client, queueItemId, DownloadClientActions.SetCategory, client.Name));

    public virtual Task<DownloadClientCategoryCheckResult> CheckKeepSeedingCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken)
        => Task.FromResult(new DownloadClientCategoryCheckResult(
            client.Id, client.Name, category, DownloadClientCategoryStatuses.Unsupported,
            $"{client.Name} is a Usenet client, and only a torrent can keep seeding.", Supported: false, Found: false));

    /// <summary>Not supported by default (#816); a client that can list its files overrides this.</summary>
    public virtual Task<IReadOnlyList<string>?> GetFilesAsync(
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    public abstract string NormalizeStatus(
        string? nativeStatus,
        double? progress,
        int? errorCode = null,
        string? errorMessage = null);

    /// <summary>
    /// #813: clears a client's own queue-level warnings (SABnzbd's
    /// mode=warnings&amp;name=clear). Most clients have no such concept, so
    /// this defaults to "not supported" rather than every adapter overriding
    /// it to say so.
    /// </summary>
    public virtual Task<bool> ClearWarningsAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <summary>Resumes the whole client. Only clients that report a client-wide pause override this.</summary>
    public virtual Task<ClientResumeResult> ResumeClientAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult(ClientResumeResult.NotSupported);

    protected DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null,
        IntegrationFailure? failure = null,
        long? freeSpaceBytes = null,
        bool? clientPaused = null,
        string? clientVersion = null,
        IReadOnlyList<string>? clientWarnings = null,
        bool? alternateSpeedLimitEnabled = null)
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
            LastFailure = failure,
            FreeSpaceBytes = freeSpaceBytes,
            ClientPaused = clientPaused,
            ClientVersion = clientVersion,
            ClientWarnings = clientWarnings,
            AlternateSpeedLimitEnabled = alternateSpeedLimitEnabled
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
