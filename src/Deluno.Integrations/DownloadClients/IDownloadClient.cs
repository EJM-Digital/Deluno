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
}
