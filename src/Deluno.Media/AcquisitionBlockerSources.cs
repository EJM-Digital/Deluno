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
        string? ProcessorName);

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

        var open = recent.Items
            .Where(dispatch => !string.Equals(dispatch.ImportStatus, "completed", StringComparison.OrdinalIgnoreCase))
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
            handoff?.ProcessorName);
    }

    /// <summary>
    /// A hand-off for this release that has not finished. A finished one is not
    /// in the way — it is the record of a completed cycle.
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
            !string.Equals(handoff.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(handoff.Status, "failed", StringComparison.OrdinalIgnoreCase));
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
