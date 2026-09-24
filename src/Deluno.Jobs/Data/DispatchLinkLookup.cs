using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The one way to find the grab behind a download (#617): by the id its client
/// gave it, then by name. Five places asked by name alone, and a real torrent's
/// name differs from the indexer's in punctuation.
/// </summary>
public static class DispatchLinkLookup
{
    public static async Task<DispatchCatalogueLink?> FindDispatchLinkForDownloadAsync(
        this IJobQueueRepository jobs,
        string downloadClientId,
        string? clientItemId,
        string releaseName,
        CancellationToken cancellationToken)
        => (string.IsNullOrWhiteSpace(clientItemId)
               ? null
               : await jobs.FindDispatchLinkByClientItemAsync(downloadClientId, clientItemId, cancellationToken))
           ?? await jobs.FindRecentDispatchLinkAsync(downloadClientId, releaseName, cancellationToken);
}
