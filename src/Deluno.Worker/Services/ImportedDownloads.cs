using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Whether the latest fetch of a release from a client has already been
/// imported (#1155). Deluno is the main stop against duplicate processing: a
/// download whose title already holds the file imported from it is never handed
/// to a processor again, and a processor's second copy is not imported again.
///
/// <para>The latest, because fetching a release again is a new acquisition: a
/// newer record that has not been imported outranks an older one that was, so a
/// genuine upgrade (a different release, or the same one fetched again on
/// purpose) is unaffected. Archived records count, because archiving is
/// housekeeping and says nothing about the outcome.</para>
/// </summary>
internal static class ImportedDownloads
{
    private const int DispatchWindow = 500;

    public static async Task<bool> WasImportedAsync(
        IJobQueueRepository jobQueueRepository,
        string clientId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(
            DispatchWindow, null, cancellationToken, includeArchived: true, clientId: clientId);
        return dispatches is not null && WasImported(dispatches, clientId, releaseName);
    }

    public static bool WasImported(IReadOnlyList<DownloadDispatchItem> dispatches, string clientId, string releaseName)
    {
        var latest = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, clientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();
        return latest is not null
            && string.Equals(latest.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether this release was sent to this client again, on purpose, after <paramref name="sinceUtc"/> and that
    /// send has not been imported (#1182). The torrent can still be in the client, finished: a title whose file
    /// left the library (a wrong episode moved to the recycle bin, say) is Missing again, search picks the same
    /// release, and the client already has it. That send is a new acquisition, so the old hand-off does not stand
    /// in for it.
    /// </summary>
    public static async Task<bool> SentAgainSinceAsync(
        IJobQueueRepository jobQueueRepository,
        string clientId,
        string releaseName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(
            DispatchWindow, null, cancellationToken, includeArchived: true, clientId: clientId);
        return dispatches is not null && Deluno.Integrations.DownloadClients.DownloadAttempts.SentAgainSince(dispatches, clientId, releaseName, sinceUtc);
    }
}
