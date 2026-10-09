using Deluno.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A seeding download whose hardlink into the library has been lost (#819):
/// configuration said it should still be sharing (same drive, hardlinks on),
/// but the real files say the library's copy was upgraded or deleted. Read
/// from the sharing pass's own last snapshot, which already did the real
/// disk check (#926's shared <c>IHardlinkInspector</c>) - this only surfaces
/// it, at no extra IO cost, and distinctly from a missing library file,
/// since the two mean different things: this download is still on disk and
/// safe to reclaim, a missing library file has nothing to reclaim from.
/// </summary>
public sealed class DownloadHardlinkHealthCheck(IDownloadSharingRepository sharing) : IHealthCheck
{
    public string Category => HealthCheckCategories.DownloadClient;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var snapshot = await sharing.GetSnapshotAsync(cancellationToken);
        var unlinked = snapshot.Holds.Where(hold => hold.LostHardlink).ToArray();
        return unlinked.Select(hold => HealthCheckOutcome.Problem(
            $"download-hardlink-unlinked:{hold.ClientId}:{hold.QueueItemId}",
            Category,
            $"{hold.Title} (unlinked from library)",
            $"{hold.Title} on {hold.ClientName} no longer shares its file data with your library.",
            cause: $"The library copy of {hold.Title} was upgraded or deleted after this download on {hold.ClientName} was linked to it.",
            fix: $"{hold.Title} is safe to reclaim: open it in Transfers and choose Remove this copy. The library file is not touched.",
            detailsJson: $$"""{"clientId":"{{hold.ClientId}}","queueItemId":"{{hold.QueueItemId}}","sizeBytes":{{hold.SizeBytes}}}"""))
            .ToArray();
    }
}
