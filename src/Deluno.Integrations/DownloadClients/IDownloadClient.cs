using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The protocol-specific boundary for one external download client.
/// Implementations are deliberately stateless; all client configuration is supplied
/// for each call so a singleton can safely serve every configured connection.
/// </summary>
public interface IDownloadClient
{
    string Protocol { get; }

    DownloadClientTelemetryCapabilities Capabilities { get; }

    Task<DownloadClientGrabResult> GrabAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken);

    Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken);

    Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken);

    string NormalizeStatus(
        string? nativeStatus,
        double? progress,
        int? errorCode = null,
        string? errorMessage = null);

    /// <summary>
    /// #813: clears a client's own queue-level warnings (SABnzbd's
    /// mode=warnings&amp;name=clear). A default interface method rather than
    /// a required member, so most clients — which have no such concept — and
    /// every existing test double need not implement it.
    /// </summary>
    Task<bool> ClearWarningsAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
