using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

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
    Func<ImportRecoveryCaseScope, string, CancellationToken, Task<int>> SettleRecoveryCases);

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
    IMediaSubtitleRepository? subtitleRepository = null)
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

        // #899/#900/#902/#903: the title is gone (or no longer wanted), so an
        // in-flight download for it is never left running, untracked, at the
        // client. Never blocklisted and never searched again - the release
        // was not bad, the title just is not wanted any more.
        metadata["cancelledClientDownloadCount"] = (await CancelActiveDownloadsAsync(title.EntityType, title.Id, cancellationToken)).ToString();

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

        var grabs = await ArchiveGrabsAsync(title.EntityType, title.Id, title.Title, cancellationToken);
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
    /// </summary>
    public async Task<Dictionary<string, string?>> CancelDownloadForUnmonitoredTitleAsync(
        string entityType,
        string entityId,
        string title,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string?>();
        var cancelled = await CancelActiveDownloadsAsync(entityType, entityId, cancellationToken);
        metadata["cancelledClientDownloadCount"] = cancelled.ToString();

        if (cancelled == 0)
        {
            return metadata;
        }

        var grabs = await ArchiveGrabsAsync(entityType, entityId, title, cancellationToken);
        metadata["archivedGrabCount"] = grabs.Archived.ToString();
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
    /// Everything the title ever fetched, which is the only way back to the work
    /// still going on about it: nothing a download leaves behind records which
    /// title it was for.
    /// </summary>
    /// <param name="Archived">How many were still open and were archived now.</param>
    private sealed record Grabs(IReadOnlyCollection<string> DispatchIds, IReadOnlyCollection<string> ReleaseNames, int Archived)
    {
        public static readonly Grabs None = new([], [], 0);
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
    private async Task<Grabs> ArchiveGrabsAsync(string entityType, string entityId, string title, CancellationToken cancellationToken)
    {
        if (dispatchesRepository is null)
        {
            return Grabs.None;
        }

        var dispatchIds = new List<string>();
        var releaseNames = new HashSet<string>(StringComparer.Ordinal);
        var archived = 0;
        var pagination = new DispatchPaginationOptions { PageSize = 200 };
        do
        {
            var (items, next) = await dispatchesRepository.QueryDispatchesAsync(
                new DispatchQueryFilter { EntityType = entityType, EntityId = entityId, IncludeArchived = true },
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
    private async Task<int> CancelActiveDownloadsAsync(string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (dispatchesRepository is null || downloadClientTelemetry is null)
        {
            return 0;
        }

        var cancelled = 0;
        var pagination = new DispatchPaginationOptions { PageSize = 200 };
        do
        {
            var (items, next) = await dispatchesRepository.QueryDispatchesAsync(
                new DispatchQueryFilter { EntityType = entityType, EntityId = entityId },
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
                    new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, dispatch.TorrentHashOrItemId),
                    cancellationToken);
                if (result.Succeeded)
                {
                    cancelled++;
                }
            }

            pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
        }
        while (pagination.PageToken is not null);

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
