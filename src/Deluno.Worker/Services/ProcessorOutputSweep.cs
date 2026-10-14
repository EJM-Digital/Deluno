using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Clears what a processor left in its output folder that Deluno knows it will
/// never import (#1155): the output of a hand-off that a newer one replaced, of a
/// hand-off whose original was imported instead, and of a download whose title (or
/// library) was removed. Anything else the processor left stays on System &#8594;
/// Health, listed for a person to import or clear (the processor-output health check).
///
/// <para>Deliberately narrow. It only ever touches a path inside the library's own
/// processor output folder, only when that path is recognisably one hand-off's
/// (its name is one of the folder names the processor gives that hand-off's
/// download), and never a file that is still being written or that an import is
/// already waiting for. Each thing removed is one line in Activity, in plain words.</para>
/// </summary>
public sealed class ProcessorOutputSweep(
    ILibrariesRepository libraries,
    IProcessorRepository processors,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeed,
    IHealthCheckRepository healthChecks)
{
    public const string ActivityCategory = "processing.output.deleted";

    /// <summary>How many of a library's hand-offs, newest first, are looked at to recognise an item.</summary>
    private const int HandoffHistory = 500;

    private const int JobHistory = 600;

    private const int DispatchHistory = 1000;

    /// <returns>How many items (a folder, a file) were removed.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var allLibraries = await libraries.ListLibrariesAsync(cancellationToken);
        var refineLibraries = allLibraries
            .Where(library => ImportWorkflows.HandsToProcessor(library.ImportWorkflow) && !string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
            .ToArray();
        if (refineLibraries.Length == 0)
        {
            return 0;
        }

        var reserved = (await jobQueueRepository.ListAsync(JobHistory, cancellationToken))
            .Where(job => job.JobType == "filesystem.import.execute" && !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase))
            .Select(job => WorkPlanner.TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => WorkPlanner.NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(DispatchHistory, null, cancellationToken, includeArchived: true);

        var removed = 0;
        foreach (var library in refineLibraries)
        {
            var root = library.ProcessorOutputPath!;
            if (!Directory.Exists(root))
            {
                continue;
            }

            var handoffs = await processors.ListProcessorHandoffsAsync(library.Id, HandoffHistory, cancellationToken);
            removed += await SweepLibraryAsync(library, root, handoffs, allLibraries, dispatches, reserved, cancellationToken);
        }

        return removed;
    }

    private async Task<int> SweepLibraryAsync(
        LibraryItem library,
        string root,
        IReadOnlyList<ProcessorHandoffItem> handoffs,
        IReadOnlyList<LibraryItem> allLibraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        ISet<string> reserved,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> entries;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
            if (string.IsNullOrWhiteSpace(name) || !IsDirectChildOf(root, entry) || IsLink(entry))
            {
                continue;
            }

            var claiming = handoffs.Where(handoff => ProcessorOutputLayout.ClaimsTopLevelName(handoff.SourcePath, library.DownloadsPath, name)).ToArray();
            if (claiming.Length == 0)
            {
                // Not recognisably a hand-off's: left to the safety net, which lists it.
                continue;
            }

            // A hand-off still with the processor, or that may still receive its output late, owns this
            // item: it is that hand-off's to import, whatever an earlier cycle of the same download left.
            if (claiming.Any(handoff => ProcessorHandoffStatuses.AcceptsLateOutput(handoff.Status)))
            {
                continue;
            }

            var latest = claiming.OrderByDescending(handoff => handoff.CreatedUtc).First();
            var reason = await NeverImportedBecauseAsync(latest, allLibraries, dispatches, cancellationToken);
            if (reason is null || !EverythingIsSettled(entry, reserved))
            {
                continue;
            }

            if (await DeleteAsync(library, root, entry, name, reason, cancellationToken))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Why this hand-off's output will never be imported, or null when it might still be.</summary>
    private async Task<string?> NeverImportedBecauseAsync(ProcessorHandoffItem handoff, IReadOnlyList<LibraryItem> allLibraries, IReadOnlyList<DownloadDispatchItem> dispatches, CancellationToken cancellationToken)
    {
        if (string.Equals(handoff.Status, ProcessorHandoffStatuses.Released, StringComparison.OrdinalIgnoreCase))
        {
            return "Deluno imported the original download instead of this processed copy";
        }

        var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(handoff.ClientId, handoff.QueueItemId, handoff.ReleaseName, cancellationToken);
        var removedSinceGrab = await CatalogueImportNaming.RemovedSinceGrabAsync(dispatch, allLibraries, movieCatalogRepository, seriesCatalogRepository, cancellationToken);
        if (removedSinceGrab is not null)
        {
            return $"{removedSinceGrab}, so Deluno will never import it";
        }

        if (ImportedDownloads.WasImported(dispatches, handoff.ClientId, handoff.ReleaseName))
        {
            return "Deluno already imported this download, so this is a second copy";
        }

        // Superseded: a newer download of the same title (a different release) was imported after this one.
        var own = dispatches
            .Where(dispatch => string.Equals(dispatch.DownloadClientId, handoff.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, handoff.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();
        var newer = own is null ? null : dispatches.FirstOrDefault(dispatch =>
            dispatch.CreatedUtc > own.CreatedUtc
            && string.Equals(dispatch.EntityType, own.EntityType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(dispatch.EntityId, own.EntityId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dispatch.ReleaseName, own.ReleaseName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase));
        return newer is null ? null : "a newer download of the same title has since been imported, so this older one was replaced and Deluno will never import it";
    }

    private async Task<bool> DeleteAsync(
        LibraryItem library, string root, string path, string label, string reason, CancellationToken cancellationToken, string? healthName = null)
    {
        var full = Path.GetFullPath(path);
        if (!IsInside(root, full))
        {
            return false;
        }

        string what;
        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
                what = $"the folder {label}";
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
                what = label;
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        await healthChecks.RemoveAsync($"processor-output:{library.Id}:{(healthName ?? label).ToLowerInvariant()}", cancellationToken);
        await activityFeed.RecordActivityAsync(
            ActivityCategory,
            $"Deleted {what} from the {library.Name} library's processor output folder: {reason}.",
            JsonSerializer.Serialize(new { LibraryId = library.Id, LibraryName = library.Name, Path = full, Automatic = true }),
            null,
            "library",
            library.Id,
            cancellationToken);
        return true;
    }

    /// <summary>Nothing under the item is still being written or already queued to import.</summary>
    private static bool EverythingIsSettled(string entry, ISet<string> reserved)
    {
        try
        {
            var files = Directory.Exists(entry) ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).ToArray() : [entry];
            return files.All(file => ProcessorOutputReadiness.IsReady(file) && !reserved.Contains(WorkPlanner.NormalizeSourceKey(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsDirectChildOf(string root, string entry)
        => string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry))), root, PathComparison);

    private static bool IsInside(string root, string path)
        => Deluno.Contracts.SafePath.IsInside(root, Path.GetFullPath(path));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
