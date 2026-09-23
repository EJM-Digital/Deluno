using System.Text.Json;
using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
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
    IProcessorConnectionService? processorConnections = null)
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

            var deletion = await recycleBinService.MoveAsync(trackedFiles, libraries, cancellationToken);
            metadata["deletedFileCount"] = deletion.MovedFileCount.ToString();
            metadata["deletedFolderCount"] = deletion.MovedFolderCount.ToString();
            metadata["recycleBinItemCount"] = deletion.Items.Count.ToString();
            if (deletion.Warnings.Count > 0)
            {
                metadata["fileDeletionWarnings"] = string.Join(" ", deletion.Warnings);
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

        var grabs = await ArchiveGrabsAsync(title, cancellationToken);
        metadata["archivedGrabCount"] = grabs.Archived.ToString();
        metadata["stoppedProcessorHandoffCount"] = (await StopWaitingAsync(title, grabs, cancellationToken)).ToString();
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
    private async Task<Grabs> ArchiveGrabsAsync(TitleToRemove title, CancellationToken cancellationToken)
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
                new DispatchQueryFilter { EntityType = title.EntityType, EntityId = title.Id, IncludeArchived = true },
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
                    await dispatchesRepository.ArchiveDispatchAsync(dispatch.Id, $"{title.Title} was removed from Deluno.", cancellationToken);
                    archived++;
                }
            }

            pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
        }
        while (pagination.PageToken is not null);

        return new Grabs(dispatchIds, releaseNames, archived);
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
    private async Task<int> StopWaitingAsync(TitleToRemove title, Grabs grabs, CancellationToken cancellationToken)
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
                _ => $"{title.Title} was removed from Deluno, so Deluno stopped waiting",
                cancellationToken,
                // Not "cancelled": that is a row on Transfers waiting for a
                // person, and the title it was about is gone.
                ProcessorHandoffStatuses.Dismissed);
            if (result is { Busy: false, Ended: not null }) stopped++;
        }

        return stopped;
    }
}
