using System.Text.Json;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Platform;

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
    Func<CancellationToken, Task<bool>> DeleteRow);

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
    IDownloadDispatchesRepository? dispatchesRepository = null)
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

        metadata["archivedGrabCount"] = (await ArchiveGrabsAsync(title, cancellationToken)).ToString();

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
    /// Archives the removed title's grabs that are still open.
    ///
    /// <para>A grab that stalled before the title was removed kept its alert open
    /// for good: alerts close when their grab settles or is archived, a removed
    /// title's grab never settles, and nothing in the app acknowledges one. On the
    /// rig, four grab-timeout alerts for two removed proof films stayed open
    /// (#537).</para>
    /// </summary>
    private async Task<int> ArchiveGrabsAsync(TitleToRemove title, CancellationToken cancellationToken)
    {
        if (dispatchesRepository is null)
        {
            return 0;
        }

        var archived = 0;
        var filter = new DispatchQueryFilter { EntityType = title.EntityType, EntityId = title.Id };
        var pagination = new DispatchPaginationOptions { PageSize = 200 };
        do
        {
            var (items, next) = await dispatchesRepository.QueryDispatchesAsync(filter, pagination, cancellationToken);
            foreach (var dispatch in items)
            {
                await dispatchesRepository.ArchiveDispatchAsync(dispatch.Id, $"{title.Title} was removed from Deluno.", cancellationToken);
                archived++;
            }

            pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
        }
        while (pagination.PageToken is not null);

        return archived;
    }
}
