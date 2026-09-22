using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Media;

/// <summary>
/// Finds the records outside Deluno's own tables that stop a title downloading.
///
/// <para>Shared by the two endpoints that need the same answer — the one that
/// explains a blocker and the one that clears it — so that what is offered and
/// what is acted on can never be two different readings of the same
/// situation.</para>
/// </summary>
public static class AcquisitionBlockerSources
{
    /// <summary>What a dispatch and its hand-off say is currently holding a title.</summary>
    public sealed record HeldBy(
        string? DownloadClientId,
        string? DownloadClientName,
        string? QueueItemId,
        string? HandoffId,
        string? ProcessorName,
        string? DispatchId = null);

    /// <summary>
    /// A download Deluno completed for this title in the past.
    ///
    /// <para>Read from Deluno's own dispatch record rather than by asking a
    /// download client. That is deliberate: the client may be unreachable, and
    /// "I fetched this and no longer have it" is a fact Deluno owns and can
    /// always answer, where "the client still refuses it" is a guess about
    /// somebody else's state.</para>
    /// </summary>
    public sealed record FetchedBefore(
        string? ClientId,
        string? ClientName,
        string? QueueItemId,
        string ReleaseName,
        DateTimeOffset WhenUtc);

    private static readonly HeldBy Nothing = new(null, null, null, null, null);

    /// <summary>
    /// The most recent dispatch for this title that has not finished importing,
    /// and the processor hand-off for its download if one is still open.
    ///
    /// <para><b>Only the unfinished ones.</b> A dispatch that imported is
    /// history, not an obstacle, and reporting it would send someone to delete
    /// a download that did its job.</para>
    /// </summary>
    public static async Task<HeldBy> FindAsync(
        IDownloadDispatchesRepository dispatches,
        IProcessorRepository processors,
        string mediaType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var recent = await dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { MediaType = mediaType, EntityId = entityId },
            new DispatchPaginationOptions { PageSize = 10 },
            cancellationToken);

        // A client holds only what it took. A grab that was refused or never
        // sent left nothing there: on the rig the page said "qBittorrent
        // already has a download for Big Buck Bunny" about a grab Deluno itself
        // had blocked, and offered to clear it from qBittorrent (#560). Rows
        // from before grabs were tracked have no grab status, and were sent.
        var open = recent.Items
            .Where(dispatch => dispatch.GrabStatus is null || string.Equals(dispatch.GrabStatus, "succeeded", StringComparison.OrdinalIgnoreCase))
            .Where(dispatch => !string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        if (open is null)
        {
            return Nothing;
        }

        var handoff = open.LibraryId is { Length: > 0 } libraryId && open.ImportedFilePath is null
            ? await FindOpenHandoffAsync(processors, libraryId, open.ReleaseName, cancellationToken)
            : null;

        return new HeldBy(
            open.DownloadClientId,
            open.DownloadClientName,
            open.TorrentHashOrItemId,
            handoff?.Id,
            handoff?.ProcessorName,
            open.Id);
    }

    /// <summary>
    /// A hand-off for this release that is still with the processor. Any ending
    /// is not in the way - it is the record of a finished cycle.
    ///
    /// <para>This used to be its own rule, "not completed and not failed", so a
    /// hand-off the processor rejected read as "Weir still has this file" after
    /// Weir had deleted it (seen on the rig, #497). The hand-off statuses say
    /// once what "with the processor" means; this asks them.</para>
    /// </summary>
    private static async Task<ProcessorHandoffItem?> FindOpenHandoffAsync(
        IProcessorRepository processors,
        string libraryId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        var handoffs = await processors.ListProcessorHandoffsAsync(libraryId, 50, cancellationToken);
        return handoffs.FirstOrDefault(handoff =>
            string.Equals(handoff.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase) &&
            ProcessorHandoffStatuses.IsWithProcessor(handoff.Status));
    }

    /// <summary>
    /// The most recent download Deluno actually finished for this title.
    ///
    /// <para><see cref="FindAsync"/> deliberately ignores completed dispatches,
    /// because a download that imported is history rather than an obstacle.
    /// That is right for everything it reports and wrong for exactly one case:
    /// the file is no longer here, so the completed download is the reason the
    /// client will refuse the next attempt.</para>
    /// </summary>
    public static async Task<FetchedBefore?> FindPreviousFetchAsync(
        IDownloadDispatchesRepository dispatches,
        string mediaType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var recent = await dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { MediaType = mediaType, EntityId = entityId },
            new DispatchPaginationOptions { PageSize = 10 },
            cancellationToken);

        var completed = recent.Items
            .Where(dispatch => string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.ImportCompletedUtc ?? dispatch.CreatedUtc)
            .FirstOrDefault();

        return completed is null
            ? null
            : new FetchedBefore(
                completed.DownloadClientId,
                completed.DownloadClientName,
                completed.TorrentHashOrItemId,
                completed.ReleaseName,
                completed.ImportCompletedUtc ?? completed.CreatedUtc);
    }

    /// <summary>
    /// Everything in the way of fetching this title again, as one override: what
    /// holds it now, or - when nothing does - the download Deluno finished before,
    /// which a client still remembers and would quietly refuse; its exclusions;
    /// and its refused releases.
    ///
    /// <para>One builder for the three places that force a fetch - a person's
    /// Force a re-download on a film or a show, and a tool's re-download (#540) -
    /// so what is cleared cannot drift between them.</para>
    /// </summary>
    /// <param name="mediaType"><c>movies</c> or <c>tv</c>.</param>
    public static async Task<AcquisitionOverrideRequest> OverrideRequestAsync(
        string mediaType,
        string id,
        string title,
        string? imdbId,
        IDownloadDispatchesRepository dispatches,
        IProcessorRepository processors,
        IUnifiedExclusionRepository exclusions,
        IBlockedReleaseRepository blockedReleases,
        CancellationToken cancellationToken)
    {
        var held = await FindAsync(dispatches, processors, mediaType, id, cancellationToken);
        var exclusionIds = await ExclusionIdsAsync(exclusions, mediaType, title, imdbId, cancellationToken);

        // Nothing in flight is the case this exists for: the download finished,
        // the file has gone, and the client is still refusing the release on the
        // strength of remembering it. Fall back to that completed dispatch so the
        // force has something to ask the client to forget.
        var fetchedBefore = held.DownloadClientId is { Length: > 0 }
            ? null
            : await FindPreviousFetchAsync(dispatches, mediaType, id, cancellationToken);

        return new AcquisitionOverrideRequest(
            id,
            title,
            held.HandoffId,
            held.DownloadClientId ?? fetchedBefore?.ClientId,
            held.DownloadClientName ?? fetchedBefore?.ClientName,
            held.QueueItemId ?? fetchedBefore?.QueueItemId,
            exclusionIds,
            held.DispatchId,
            (await blockedReleases.ListForAsync(mediaType, id, cancellationToken)).Select(release => release.Id).ToArray());
    }

    public static async Task<bool> IsExcludedAsync(
        IUnifiedExclusionRepository exclusions,
        string mediaType,
        string title,
        string? imdbId,
        CancellationToken cancellationToken)
        => (await MatchingExclusionsAsync(exclusions, mediaType, title, imdbId, cancellationToken)).Count > 0;

    public static async Task<IReadOnlyList<string>> ExclusionIdsAsync(
        IUnifiedExclusionRepository exclusions,
        string mediaType,
        string title,
        string? imdbId,
        CancellationToken cancellationToken)
        => (await MatchingExclusionsAsync(exclusions, mediaType, title, imdbId, cancellationToken))
            .Select(exclusion => exclusion.Id)
            .ToArray();

    /// <summary>
    /// Matched on the IMDb id where there is one, and on the title otherwise.
    /// The id is the reliable half; the title is what an exclusion added by
    /// hand will usually carry.
    /// </summary>
    private static async Task<IReadOnlyList<MediaExclusionItem>> MatchingExclusionsAsync(
        IUnifiedExclusionRepository exclusions,
        string mediaType,
        string title,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        var active = await exclusions.ListActiveAsync(mediaType, null, null, cancellationToken);
        return active
            .Where(exclusion =>
                (!string.IsNullOrWhiteSpace(imdbId) &&
                 string.Equals(exclusion.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(exclusion.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
