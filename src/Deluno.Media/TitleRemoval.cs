using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Media;

/// <param name="DeleteFiles">Move the title's tracked files to the recycle bin.</param>
/// <param name="AddImportListExclusion">Stop the import lists that added it from adding it again.</param>
/// <param name="Reason">
/// Why it was removed, when something other than a person asked: a retention
/// rule says so here, and the activity entry carries it.
/// </param>
public sealed record TitleRemovalOptions(bool DeleteFiles, bool AddImportListExclusion, string? Reason = null);

/// <summary>
/// One film or show to remove, with the two things only its own catalogue can
/// do: list the files it holds, and delete its row.
/// </summary>
/// <param name="MediaType"><c>movies</c> or <c>tv</c>.</param>
/// <param name="EntityType"><c>movie</c> or <c>series</c>.</param>
public sealed record TitleToRemove(
    string MediaType,
    string EntityType,
    string Id,
    string Title,
    int? Year,
    string? ImdbId,
    Func<string, CancellationToken, IAsyncEnumerable<string>> TrackedFilesInLibrary,
    Func<CancellationToken, Task<bool>> DeleteRow,
    /// <summary>
    /// Closes the open recovery cases about the title's downloads. Only its own
    /// catalogue holds them, and a case records a release name rather than which
    /// title it belongs to — so the scope names the downloads (#563).
    /// </summary>
    Func<ImportRecoveryCaseScope, string, CancellationToken, Task<int>> SettleRecoveryCases,
    /// <summary>
    /// #947: a series' own dispatches are recorded under its own id, but the
    /// most common TV grab — an automatic per-episode search — is recorded
    /// under its episode's id instead (EntityType "episode"). Only a series'
    /// own catalogue (<see cref="Deluno.Media"/> cannot reach it — see the
    /// callers) knows its episode ids, so a series-removal target supplies
    /// them here; null/empty for a movie, and for a series with none.
    /// </summary>
    Func<CancellationToken, Task<IReadOnlyList<string>>>? RelatedEpisodeIds = null);

/// <summary>
/// Removing a title from Deluno.
///
/// <para>It was two private methods, one in the movies endpoints and one in the
/// series endpoints, identical but for the names. Retention (#492) is a third
/// caller, and a third copy is how the three would start to disagree about what
/// "removed" means - so there is one, and the endpoints and the rules all use
/// it.</para>
///
/// <para>Files go to the recycle bin, not away: a removal can be undone until
/// the bin's retention runs out.</para>
/// </summary>
public sealed class TitleRemovalService(
    ILibrariesRepository librariesRepository,
    IIntakeRepository intakeRepository,
    IJobQueueRepository jobQueueRepository,
    IActivityFeedRepository activityFeedRepository,
    IRecycleBinService recycleBinService,
    IDownloadDispatchesRepository? dispatchesRepository = null,
    IProcessorRepository? processorRepository = null,
    IProcessorConnectionService? processorConnections = null,
    // #662: the only place besides retention a title's files leave the
    // library. Queued, same as an import's notify - a down media server must
    // never hold up removing a title.
    IJobScheduler? mediaServerJobScheduler = null,
    IDownloadClientTelemetryService? downloadClientTelemetry = null,
    IMediaSubtitleRepository? subtitleRepository = null,
    // #856: a title removed with its files is the same "file delete" concept
    // as a replaced file's own delete, whether a person asked for it here or
    // a retention rule did (the only two callers of this service).
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationService = null,
    // #990: this title's own tags, read before its row is deleted below, so a
    // tagged notification connection still fires for a removal it is scoped
    // to.
    IMediaTagStore? mediaTagStore = null,
    ILogger<TitleRemovalService>? logger = null)
{
    public async Task<Dictionary<string, string?>> RemoveAsync(
        TitleToRemove title,
        TitleRemovalOptions options,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string?>();
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var cancelledJobs = await jobQueueRepository.CancelPendingForRelatedEntityAsync(title.EntityType, title.Id, cancellationToken);
        metadata["cancelledPendingJobCount"] = cancelledJobs.ToString();

        // #947: removing a series also has to reach the dispatches recorded
        // under its episodes, not just its own id - the most common TV grab
        // (an automatic per-episode search) is recorded there.
        var relatedEpisodeIds = title.RelatedEpisodeIds is null
            ? []
            : await title.RelatedEpisodeIds(cancellationToken);

        // #899/#900/#902/#903: the title is gone (or no longer wanted), so an
        // in-flight download for it is never left running, untracked, at the
        // client. Never blocklisted and never searched again - the release
        // was not bad, the title just is not wanted any more.
        metadata["cancelledClientDownloadCount"] =
            (await CancelActiveDownloadsAsync(title.EntityType, title.Id, relatedEpisodeIds, cancellationToken)).ToString();

        if (options.DeleteFiles)
        {
            var trackedFiles = new List<TrackedLibraryFile>();
            foreach (var library in libraries)
            {
                await foreach (var path in title.TrackedFilesInLibrary(library.Id, cancellationToken))
                {
                    trackedFiles.Add(new TrackedLibraryFile(library.Id, path));
                }
            }

            // #893's rule again: subtitles follow the fate of the video they
            // belong to. Here that is every subtitle the title has, since the
            // whole title is being removed, not just one file replaced.
            if (subtitleRepository is not null && trackedFiles.Count > 0)
            {
                var kind = title.EntityType == "series" ? MediaKind.Series : MediaKind.Movie;
                var subtitles = await subtitleRepository.ListSubtitlesAsync(kind, title.Id, cancellationToken);
                var subtitleLibraryId = trackedFiles[0].LibraryId;
                foreach (var subtitle in subtitles)
                {
                    if (subtitle.FilePath is { Length: > 0 } subtitlePath)
                    {
                        trackedFiles.Add(new TrackedLibraryFile(subtitleLibraryId, subtitlePath));
                    }
                }
            }

            var deletion = await recycleBinService.MoveAsync(trackedFiles, libraries, cancellationToken);
            metadata["deletedFileCount"] = deletion.MovedFileCount.ToString();
            metadata["deletedFolderCount"] = deletion.MovedFolderCount.ToString();
            metadata["recycleBinItemCount"] = deletion.Items.Count.ToString();
            if (deletion.Warnings.Count > 0)
            {
                metadata["fileDeletionWarnings"] = string.Join(" ", deletion.Warnings);
            }

            // #662: manual removal is the other path a title's files leave
            // the library by, alongside retention - and until now the only
            // one that told a media server about it.
            if (mediaServerJobScheduler is not null && trackedFiles.Count > 0)
            {
                var payload = new MediaServerRefreshJobPayload(
                    title.MediaType,
                    trackedFiles.Select(file => new MediaServerPathChange(file.FilePath, MediaServerChangeKind.Deleted)).ToArray());
                await mediaServerJobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        MediaServerJobTypes.Refresh,
                        Source: "title.removed",
                        PayloadJson: JsonSerializer.Serialize(payload),
                        RelatedEntityType: title.EntityType,
                        RelatedEntityId: title.Id),
                    cancellationToken);
            }

            if (trackedFiles.Count > 0)
            {
                var kind = title.EntityType == "series" ? MediaKind.Series : MediaKind.Movie;
                var titleTags = mediaTagStore is null
                    ? null
                    : (await mediaTagStore.ListAsync(kind, title.Id, cancellationToken)).Select(assignment => assignment.Name).ToArray();
                await NotificationOutboundDispatch.DispatchIfEnabledAsync(
                    outboundNotificationService,
                    notificationService,
                    "file.deleted",
                    $"Files removed: {title.Title}",
                    options.Reason is { Length: > 0 } deleteReason
                        ? $"{title.Title}'s files were removed: {deleteReason}."
                        : $"{title.Title}'s files were removed from Deluno.",
                    JsonSerializer.Serialize(new { title.Title, title.EntityType, title.Id, fileCount = trackedFiles.Count, options.Reason }),
                    prefs => prefs.FileDeletedEnabled,
                    "title-removal",
                    logger ?? NullLogger<TitleRemovalService>.Instance,
                    cancellationToken,
                    titleTags);
            }
        }

        if (options.AddImportListExclusion)
        {
            var origins = await intakeRepository.ListIntakeTitleOriginsAsync(title.MediaType, title.Id, cancellationToken);
            var exclusionsAdded = 0;
            var exclusionWarnings = new List<string>();
            foreach (var origin in origins.GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                try
                {
                    var exclusion = await intakeRepository.CreateIntakeListExclusionAsync(
                        origin.SourceId,
                        new CreateIntakeListExclusionRequest(title.Title, title.Year, title.ImdbId, null, options.Reason ?? "Removed from library by user"),
                        cancellationToken);
                    if (exclusion is not null) exclusionsAdded++;
                }
                catch
                {
                    exclusionWarnings.Add($"Deluno could not add the exclusion for {origin.SourceName}.");
                }
            }

            metadata["importListExclusionsAdded"] = exclusionsAdded.ToString();
            if (exclusionWarnings.Count > 0)
            {
                metadata["importListExclusionWarnings"] = string.Join(" ", exclusionWarnings);
            }
        }

        if (!await title.DeleteRow(cancellationToken))
        {
            throw new InvalidOperationException(title.EntityType == "series" ? "TV show was not removed from Deluno." : "Movie was not removed from Deluno.");
        }

        var grabs = await ArchiveGrabsAsync(title.EntityType, title.Id, title.Title, relatedEpisodeIds, cancellationToken);
        metadata["archivedGrabCount"] = grabs.Archived.ToString();
        metadata["stoppedProcessorHandoffCount"] = (await StopWaitingAsync(title.Title, grabs, cancellationToken)).ToString();
        metadata["settledRecoveryCaseCount"] = (await title.SettleRecoveryCases(
            ImportRecoveryCaseScope.ForRemovedTitle(grabs.DispatchIds, grabs.ReleaseNames),
            $"{title.Title} was removed from Deluno, so this is no longer something to answer.",
            cancellationToken)).ToString();

        var message = options.Reason is { Length: > 0 } reason
            ? $"{title.Title} was removed from Deluno: {reason}"
            : $"{title.Title} was removed from Deluno.{(options.DeleteFiles ? " Imported library files were also selected for deletion." : string.Empty)}";
        await activityFeedRepository.RecordActivityAsync(
            $"{title.EntityType}.removed",
            message,
            JsonSerializer.Serialize(new { options.DeleteFiles, options.AddImportListExclusion, options.Reason, metadata }),
            null,
            title.EntityType,
            title.Id,
            cancellationToken);

        return metadata;
    }

    /// <summary>
    /// #900: unmonitoring a title mid-download stops wanting it right now, not
    /// only at the next automatic pass. Its in-flight download (a first grab
    /// or an upgrade in progress) is cancelled at the client with its data,
    /// any processor already working on it is dropped, and its own dispatch
    /// is archived so nothing later treats the cancelled download as a
    /// download that vanished. The library file, if any, is left alone -
    /// cancelling an upgrade keeps the current file. Never blocklisted, never
    /// searched again: the release was not bad, the title just is not wanted
    /// any more.
    ///
    /// <para>Does nothing, and reports nothing, when the title had no active
    /// download to cancel - unmonitoring a title with nothing in flight is
    /// not itself news.</para>
    ///
    /// <para>#947: unmonitoring a whole series also has to reach the
    /// dispatches recorded under its episodes (<paramref name="relatedEpisodeIds"/>
    /// - the most common TV grab is recorded there, not under the series'
    /// own id. Unmonitoring one episode also has to consider a season-pack
    /// dispatch recorded under its series (<paramref name="seasonPack"/>):
    /// that pack is cancelled with the rest only once every episode it
    /// covers is unmonitored, otherwise it is left running for the episodes
    /// still wanted and Activity says so.</para>
    /// </summary>
    public async Task<Dictionary<string, string?>> CancelDownloadForUnmonitoredTitleAsync(
        string entityType,
        string entityId,
        string title,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? relatedEpisodeIds = null,
        SeasonPackUnmonitorContext? seasonPack = null)
    {
        var metadata = new Dictionary<string, string?>();
        var episodeIds = relatedEpisodeIds ?? [];
        var cancelled = await CancelActiveDownloadsAsync(entityType, entityId, episodeIds, cancellationToken);

        var seasonPackOutcome = await HandleSeasonPackForUnmonitoredEpisodeAsync(entityId, title, seasonPack, cancellationToken);
        cancelled += seasonPackOutcome.Cancelled;

        metadata["cancelledClientDownloadCount"] = cancelled.ToString();

        var grabs = Grabs.None;
        if (cancelled > 0)
        {
            grabs = await ArchiveGrabsAsync(entityType, entityId, title, episodeIds, cancellationToken);
        }

        grabs = grabs.Merge(seasonPackOutcome.Grabs);
        metadata["archivedGrabCount"] = grabs.Archived.ToString();

        if (cancelled == 0 && grabs.Archived == 0)
        {
            return metadata;
        }

        metadata["stoppedProcessorHandoffCount"] = (await StopWaitingAsync(
            title,
            grabs,
            cancellationToken,
            stoppedWaitingReason: "is no longer monitored")).ToString();

        await activityFeedRepository.RecordActivityAsync(
            $"{entityType}.download-cancelled",
            $"{title} is no longer monitored, so Deluno cancelled its download and discarded any processed output.",
            JsonSerializer.Serialize(new { metadata }),
            null,
            entityType,
            entityId,
            cancellationToken);

        return metadata;
    }

    /// <summary>
    /// What unmonitoring one episode needs to know to also settle a
    /// season-pack dispatch recorded under its series (#947): the series to
    /// look for open season-pack dispatches under, and how to ask whether
    /// every episode a given pack covers is now unmonitored (the caller's
    /// catalogue, not <see cref="Deluno.Media"/>, holds monitored state).
    /// Null when the caller has nothing to check (no dispatches repository,
    /// or the episode's series is unknown).
    /// </summary>
    public sealed record SeasonPackUnmonitorContext(
        string SeriesId,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>> AreAllEpisodesUnmonitored);

    /// <summary>
    /// A season-pack dispatch (EntityType "series") that names the episode
    /// being unmonitored among the ones it covers (#947). Cancelled, archived
    /// and dismissed only once every episode it covers is unmonitored - a
    /// pack still wanted for other episodes is left running, and Activity
    /// says so rather than the person losing track of why it is still there.
    /// </summary>
    private async Task<(int Cancelled, Grabs Grabs)> HandleSeasonPackForUnmonitoredEpisodeAsync(
        string episodeId,
        string title,
        SeasonPackUnmonitorContext? seasonPack,
        CancellationToken cancellationToken)
    {
        if (seasonPack is null || dispatchesRepository is null)
        {
            return (0, Grabs.None);
        }

        var cancelled = 0;
        var dispatchIds = new List<string>();
        var releaseNames = new HashSet<string>(StringComparer.Ordinal);
        var archived = 0;
        var pagination = new DispatchPaginationOptions { PageSize = 200 };
        do
        {
            var (items, next) = await dispatchesRepository.QueryDispatchesAsync(
                new DispatchQueryFilter { EntityType = "series", EntityId = seasonPack.SeriesId },
                pagination,
                cancellationToken);
            foreach (var dispatch in items)
            {
                if (string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var coveredEpisodeIds = ReplacementSearchQueuer.ExtractSeasonEpisodeIds(dispatch.NotesJson);
                if (!coveredEpisodeIds.Contains(episodeId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!await seasonPack.AreAllEpisodesUnmonitored(coveredEpisodeIds, cancellationToken))
                {
                    await activityFeedRepository.RecordActivityAsync(
                        "series.season-pack-kept-running",
                        $"{title} is no longer monitored, but its season pack keeps downloading: other episodes it covers are still monitored.",
                        JsonSerializer.Serialize(new { dispatch.Id, coveredEpisodeIds }),
                        null,
                        "series",
                        seasonPack.SeriesId,
                        cancellationToken);
                    continue;
                }

                if (downloadClientTelemetry is not null &&
                    !string.IsNullOrWhiteSpace(dispatch.DownloadClientId) &&
                    !string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
                {
                    var result = await downloadClientTelemetry.ExecuteActionAsync(
                        dispatch.DownloadClientId,
                        new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, dispatch.TorrentHashOrItemId, dispatch.Id),
                        cancellationToken);
                    if (result.Succeeded)
                    {
                        cancelled++;
                    }
                }

                dispatchIds.Add(dispatch.Id);
                if (dispatch.ReleaseName is { Length: > 0 } releaseName)
                {
                    releaseNames.Add(releaseName);
                }

                await dispatchesRepository.ArchiveDispatchAsync(dispatch.Id, $"{title} is no longer monitored, and no other episode its season pack covers still is.", cancellationToken);
                archived++;
            }

            pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
        }
        while (pagination.PageToken is not null);

        return (cancelled, new Grabs(dispatchIds, releaseNames, archived));
    }

    /// <summary>
    /// Everything the title ever fetched, which is the only way back to the work
    /// still going on about it: nothing a download leaves behind records which
    /// title it was for.
    /// </summary>
    /// <param name="Archived">How many were still open and were archived now.</param>
    private sealed record Grabs(IReadOnlyCollection<string> DispatchIds, IReadOnlyCollection<string> ReleaseNames, int Archived)
    {
        public static readonly Grabs None = new([], [], 0);

        /// <summary>Combines this and a season pack's own outcome (#947) into one report.</summary>
        public Grabs Merge(Grabs other)
        {
            if (other.DispatchIds.Count == 0 && other.ReleaseNames.Count == 0 && other.Archived == 0)
            {
                return this;
            }

            var releaseNames = new HashSet<string>(ReleaseNames, StringComparer.Ordinal);
            releaseNames.UnionWith(other.ReleaseNames);
            return new Grabs([.. DispatchIds, .. other.DispatchIds], releaseNames, Archived + other.Archived);
        }
    }

    /// <summary>
    /// Archives the removed title's grabs that are still open, and reports every
    /// grab it ever made.
    ///
    /// <para>A grab that stalled before the title was removed kept its alert open
    /// for good: alerts close when their grab settles or is archived, a removed
    /// title's grab never settles, and nothing in the app acknowledges one. On the
    /// rig, four grab-timeout alerts for two removed proof films stayed open
    /// (#537).</para>
    ///
    /// <para>The archived ones are read too, and only read: a successful import
    /// archives its dispatch at once, so the grabs a title made across a week are
    /// mostly archived — and a case raised about one of them is still open and
    /// still counted (#563).</para>
    /// </summary>
    private async Task<Grabs> ArchiveGrabsAsync(
        string entityType,
        string entityId,
        string title,
        IReadOnlyList<string> relatedEpisodeIds,
        CancellationToken cancellationToken)
    {
        if (dispatchesRepository is null)
        {
            return Grabs.None;
        }

        var dispatchIds = new List<string>();
        var releaseNames = new HashSet<string>(StringComparer.Ordinal);
        var archived = 0;

        // #947: a series' episode-level dispatches (single-episode searches)
        // are recorded under the episode's own id, not the series'.
        var targets = new List<(string EntityType, string EntityId)> { (entityType, entityId) };
        targets.AddRange(relatedEpisodeIds.Select(episodeId => ("episode", episodeId)));

        foreach (var (targetType, targetId) in targets)
        {
            var pagination = new DispatchPaginationOptions { PageSize = 200 };
            do
            {
                var (items, next) = await dispatchesRepository.QueryDispatchesAsync(
                    new DispatchQueryFilter { EntityType = targetType, EntityId = targetId, IncludeArchived = true },
                    pagination,
                    cancellationToken);
                foreach (var dispatch in items)
                {
                    dispatchIds.Add(dispatch.Id);
                    if (dispatch.ReleaseName is { Length: > 0 } releaseName)
                    {
                        releaseNames.Add(releaseName);
                    }

                    if (!string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase))
                    {
                        await dispatchesRepository.ArchiveDispatchAsync(dispatch.Id, $"{title} was removed from Deluno.", cancellationToken);
                        archived++;
                    }
                }

                pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
            }
            while (pagination.PageToken is not null);
        }

        return new Grabs(dispatchIds, releaseNames, archived);
    }

    /// <summary>
    /// Stops an in-flight download for the removed title at its client,
    /// with its data — the title is gone, so nothing here is worth keeping,
    /// and nothing here was the release's fault: never blocklisted, never
    /// searched again (#899/#900/#902/#903).
    ///
    /// <para>Only a dispatch not yet imported and not already archived is
    /// live enough to still be at the client. One already imported has
    /// nothing left running; one already archived has already been settled by
    /// an earlier removal or by the client itself.</para>
    /// </summary>
    private async Task<int> CancelActiveDownloadsAsync(
        string entityType,
        string entityId,
        IReadOnlyList<string> relatedEpisodeIds,
        CancellationToken cancellationToken)
    {
        if (dispatchesRepository is null || downloadClientTelemetry is null)
        {
            return 0;
        }

        var cancelled = 0;

        // #947: a series' episode-level dispatches (single-episode searches)
        // are recorded under the episode's own id, not the series'.
        var targets = new List<(string EntityType, string EntityId)> { (entityType, entityId) };
        targets.AddRange(relatedEpisodeIds.Select(episodeId => ("episode", episodeId)));

        foreach (var (targetType, targetId) in targets)
        {
            var pagination = new DispatchPaginationOptions { PageSize = 200 };
            do
            {
                var (items, next) = await dispatchesRepository.QueryDispatchesAsync(
                    new DispatchQueryFilter { EntityType = targetType, EntityId = targetId },
                    pagination,
                    cancellationToken);
                foreach (var dispatch in items)
                {
                    if (string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(dispatch.DownloadClientId) ||
                        string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
                    {
                        continue;
                    }

                    var result = await downloadClientTelemetry.ExecuteActionAsync(
                        dispatch.DownloadClientId,
                        new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, dispatch.TorrentHashOrItemId, dispatch.Id),
                        cancellationToken);
                    if (result.Succeeded)
                    {
                        cancelled++;
                    }
                }

                pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
            }
            while (pagination.PageToken is not null);
        }

        return cancelled;
    }

    /// <summary>
    /// Ends the hand-offs the removed title still has with a processor.
    ///
    /// <para>Settling its open cases is not enough on its own: a hand-off left
    /// waiting goes on being waited for, and raises a fresh case hours later for
    /// a title nobody manages any more. So the wait is ended where the title
    /// ends, and the processor is asked to drop the file rather than keep
    /// working on it (#563).</para>
    ///
    /// <para>A processor already mid-run is left alone — its hand-off ends the
    /// ordinary way, when it answers.</para>
    /// </summary>
    private async Task<int> StopWaitingAsync(string title, Grabs grabs, CancellationToken cancellationToken, string stoppedWaitingReason = "was removed from Deluno")
    {
        if (processorRepository is null || grabs.ReleaseNames.Count == 0)
        {
            return 0;
        }

        var stopped = 0;
        var open = await processorRepository.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken);
        foreach (var handoff in open.Where(item => grabs.ReleaseNames.Contains(item.ReleaseName)))
        {
            var result = await ProcessorHandoffCancellation.StopWaitingAsync(
                processorRepository,
                processorConnections,
                activityFeedRepository,
                handoff,
                _ => $"{title} {stoppedWaitingReason}, so Deluno stopped waiting",
                cancellationToken,
                // Not "cancelled": that is a row on Transfers waiting for a
                // person, and the title it was about is gone.
                ProcessorHandoffStatuses.Dismissed,
                // #903: a title being removed is not a reason to keep waiting
                // for a processor's output, even one already mid-run - there
                // is no title left for that output to land in.
                discardEvenIfBusy: true);
            if (result.Ended is not null) stopped++;
        }

        return stopped;
    }
}
