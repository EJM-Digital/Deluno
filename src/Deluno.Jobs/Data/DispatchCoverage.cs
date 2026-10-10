using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// Whether a release a search just chose is already covered by a download on
/// its way, so no search path — show-level, season, or episode — sends a
/// second grab for the same title or the episodes a pack already covers
/// (#555, #655).
///
/// <para>Shared by <c>Deluno.Worker</c>'s three search handlers and
/// <c>Deluno.Series</c>'s <c>SeasonSearchExecutor</c>, which the automatic
/// library cycle's targeted show-level pass and its own scheduled
/// <c>series.search.season</c> job both reach for the same series within
/// seconds of each other. Every one of those paths records its dispatch
/// against the same entity id — the series id for a show-level or season
/// grab, the episode id for a per-episode grab — so a lookup keyed on the
/// entity ids a search is about to act on sees every other path's dispatch,
/// whichever of them ran first.</para>
/// </summary>
public static class DispatchCoverage
{
    /// <summary>
    /// Generous for the same reason as the reconciler's lookup: missing a live
    /// download from this list costs a second download.
    /// </summary>
    public const int InFlightLimit = 5_000;

    /// <summary>
    /// Why a release the search chose is not sent, when this title or that
    /// exact release already has a download on its way; null when nothing is.
    /// </summary>
    public static async Task<string?> AlreadyOnItsWayAsync(
        IJobQueueRepository jobQueueRepository,
        string releaseName,
        IReadOnlyCollection<string> entityIds,
        CancellationToken cancellationToken)
    {
        var inFlight = await jobQueueRepository.ListDownloadsInFlightAsync(InFlightLimit, cancellationToken) ?? [];
        // One its client never showed is not on its way; sending it again is the retry.
        var same = inFlight.Where(download => !download.NeverShown).FirstOrDefault(download =>
            entityIds.Contains(download.EntityId, StringComparer.Ordinal)
            || string.Equals(download.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase));

        return same is null
            ? null
            : string.Equals(same.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase)
                ? $"Not sent again: {releaseName} is already on its way."
                : $"Not sent: {same.ReleaseName} is already on its way for this.";
    }

    /// <summary>
    /// Why a release the search chose is not sent, when the library already holds this exact release for the title:
    /// one of its earlier sends was imported and the title still has a file. Nothing in flight shows this, since an
    /// imported send is finished, so a title removed and restored from the recycle bin was sent its own release a
    /// second time (#1202). Null when the title has no file, or none of its imports came from this release.
    /// </summary>
    public static async Task<string?> AlreadyInLibraryAsync(
        IJobQueueRepository jobQueueRepository,
        string releaseName,
        string entityId,
        bool titleHasFile,
        CancellationToken cancellationToken)
    {
        if (!titleHasFile || string.IsNullOrWhiteSpace(releaseName) || string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        var page = await jobQueueRepository.ListDownloadDispatchesPageAsync(
            new Deluno.Contracts.PageRequest(Deluno.Contracts.PageRequest.MaximumPageSize),
            null,
            cancellationToken,
            includeArchived: true,
            entityId: entityId);
        var imported = (page?.Items ?? []).Any(dispatch =>
            string.Equals(dispatch.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(dispatch.EntityId, entityId, StringComparison.Ordinal)
            && (string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dispatch.ImportStatus, "completed", StringComparison.OrdinalIgnoreCase)));

        return imported ? $"Not sent again: {releaseName} is already in the library." : null;
    }
}
