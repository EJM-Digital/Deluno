using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;

namespace Deluno.Worker.Services;

/// <inheritdoc cref="IGivenUpDownloadRemoval"/>
public sealed class GivenUpDownloadRemoval(
    IDownloadClientTelemetryService downloadClients,
    IDownloadDispatchesRepository dispatches) : IGivenUpDownloadRemoval
{
    public async Task<GivenUpDownloadRemovalResult> RemoveAsync(DownloadDispatchItem dispatch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatch.DownloadClientId) || string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
        {
            return new GivenUpDownloadRemovalResult(false, "Deluno cannot tell which download in the client to remove.");
        }

        // The ownership check is the removal's own: it refuses anything Deluno did not send.
        var result = await downloadClients.RemoveWithReasonAsync(
            dispatch.DownloadClientId,
            dispatch.TorrentHashOrItemId,
            QueueRemovalReasons.GaveUp,
            cancellationToken);
        if (!result.Succeeded)
        {
            return new GivenUpDownloadRemovalResult(false, result.Message);
        }

        // Left open, the record keeps the title "on its way" and the new search is skipped.
        await dispatches.ArchiveDispatchAsync(dispatch.Id, "Gave up on this release and removed it from the client.", cancellationToken);
        return new GivenUpDownloadRemovalResult(true, null);
    }
}
