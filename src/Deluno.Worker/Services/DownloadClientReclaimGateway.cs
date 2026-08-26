using Deluno.Integrations.DownloadClients;
using Deluno.Recovery.Services;

namespace Deluno.Worker.Services;

/// <summary>
/// The one thing the sharing rule is allowed to do to a download client: ask it
/// to let go of a finished item and the data with it (#288).
///
/// It goes through the client's own action path rather than the filesystem
/// deliberately. Deleting a file a torrent client still believes it owns leaves
/// the torrent registered against data that has vanished, so the client errors
/// it, sharing stops, and on a private site that costs the user their ratio or
/// their account (#287). Asking the client means the client tidies its own file
/// and forgets the torrent in one step.
/// </summary>
public sealed class DownloadClientReclaimGateway(IDownloadClientTelemetryService telemetry) : IDownloadClientActionGateway
{
    public async Task<DownloadClientRemovalResult> RemoveWithDataAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var result = await telemetry.ReclaimCompletedAsync(clientId, queueItemId, cancellationToken);

        return new DownloadClientRemovalResult(result.Succeeded, result.Message);
    }
}
