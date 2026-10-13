using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Deluno.Worker.Services;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A safety net, not a routine feature (#921): in normal running Deluno starts
/// every download and every import, so nothing should ever be left in a
/// library's processor output folder that no hand-off is waiting for. When
/// something is there anyway -- an interruption between hand-off and output
/// (#913, #914), an abandoned hand-off the processor finished anyway (#915), a
/// processed copy that failed to import (#919), or another app sharing the
/// folder -- this shows it once on System &#8594; Health, naming each item, so
/// it is never silently lost.
///
/// <para>Only the top level of the output folder is looked at: a file, a
/// season-pack folder, or a folder of non-media scraps. Files belonging to an
/// active hand-off (<see cref="WorkPlanner.ExpectedOutputDirectoryNames"/>) or
/// still being written (<see cref="ProcessorOutputReadiness"/>) are never
/// listed. Whatever Deluno knows it will never import is cleared first
/// (<c>ProcessorOutputSweep</c>, #1155); anything else is only listed here -- see
/// <c>ExternalIntegrationEndpointRouteBuilderExtensions</c>'s
/// <c>/processors/output/delete</c> for the only way that happens, and only on
/// request.</para>
/// </summary>
public sealed class ProcessorOutputSafetyNetHealthCheck(
    ILibrariesRepository libraries,
    IProcessorRepository processors,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movies,
    ProcessorOutputSweep? sweep = null) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    /// <summary>Same batch size <see cref="WorkPlanner"/> reads existing jobs with -- one list, read once.</summary>
    private const int MaintenancePlanningBatchSize = 600;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        // #1155: what Deluno knows it will never import (a hand-off a newer one replaced, one whose
        // original was imported, a removed title) is cleared first, each with a line in Activity.
        // What is left is listed below for a person to import or clear.
        if (sweep is not null)
        {
            try
            {
                await sweep.SweepAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The list below is still true; the sweep tries again on the next pass.
            }
        }

        var refineLibraries = (await libraries.ListLibrariesAsync(cancellationToken))
            .Where(library =>
                ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                !string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
            .ToArray();
        if (refineLibraries.Length == 0)
        {
            return [];
        }

        // A file already queued or done importing is spoken for, whichever
        // library it landed under -- the same rule WorkPlanner itself applies
        // before ever calling a loose file "unmatched".
        var reservedSourceKeys = (await jobQueueRepository.ListAsync(MaintenancePlanningBatchSize, cancellationToken))
            .Where(job =>
                job.JobType == "filesystem.import.execute" &&
                !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase))
            .Select(job => WorkPlanner.TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => WorkPlanner.NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Imports still queued or running: the only ones that keep a second copy out of sight. A finished import has
        // already done its work, and a spare copy beside it is just a spare copy (#1138).
        var activeSourceKeys = (await jobQueueRepository.ListAsync(MaintenancePlanningBatchSize, cancellationToken))
            .Where(job => job.JobType == "filesystem.import.execute"
                && !string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase))
            .Select(job => WorkPlanner.TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => WorkPlanner.NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string>? heldFilms = null;

        var allHandoffs = await processors.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.AcceptingLateOutput, cancellationToken);
        // A finished hand-off still owns the folder its output sits in (#1155), whatever the folder is called.
        var finishedHandoffs = await processors.ListProcessorHandoffsWithStatusAsync([ProcessorHandoffStatuses.Completed], cancellationToken);

        var outcomes = new List<HealthCheckOutcome>();
        foreach (var library in refineLibraries)
        {
            var root = library.ProcessorOutputPath!;
            if (!Directory.Exists(root))
            {
                continue;
            }

            var activeHandoffs = allHandoffs
                .Where(handoff => string.Equals(handoff.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var finished in finishedHandoffs.Where(handoff =>
                         string.Equals(handoff.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (OwnedTopLevelName(root, finished.OutputPath) is { } owned)
                {
                    activeNames.Add(owned);
                }
            }

            IReadOnlyList<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
                if (string.IsNullOrWhiteSpace(name) || activeNames.Contains(name)
                    || activeHandoffs.Any(handoff => ProcessorOutputLayout.ClaimsTopLevelName(handoff.SourcePath, library.DownloadsPath, name)))
                {
                    continue;
                }

                // A second copy of a film the library already holds (#1138): said as that, with Keep and Remove.
                string? spareOf = null;
                if (string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase)
                    && (Directory.Exists(entry) || WorkPlanner.IsImportableVideoFile(entry)))
                {
                    heldFilms ??= await HeldFilmsAsync(cancellationToken);
                    if (SpareCopy.TitleKey(name) is { } key && heldFilms.TryGetValue(key, out var heldTitle))
                    {
                        spareOf = heldTitle;
                    }
                }

                if (spareOf is not null)
                {
                    var spare = DescribeSpare(library, entry, name, spareOf, activeSourceKeys);
                    if (spare is not null)
                    {
                        outcomes.Add(spare);
                    }

                    continue;
                }

                var outcome = Directory.Exists(entry)
                    ? DescribeFolder(library, entry, name, reservedSourceKeys)
                    : DescribeFile(library, entry, name, reservedSourceKeys);
                if (outcome is not null)
                {
                    outcomes.Add(outcome);
                }
            }
        }

        return outcomes;
    }

    /// <summary>The folder or file directly under the output root that this output path sits in, or null when it is elsewhere.</summary>
    internal static string? OwnedTopLevelName(string root, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, outputPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) || first == "." ? null : first;
    }

    private async Task<IReadOnlyDictionary<string, string>> HeldFilmsAsync(CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var film in await movies.ListAsync(cancellationToken))
        {
            if (film.HasFile && SpareCopy.KeyOf(film.Title, film.ReleaseYear) is { } key)
            {
                held[key] = film.ReleaseYear is { } year ? $"{film.Title} ({year})" : film.Title;
            }
        }

        return held;
    }

    private static HealthCheckOutcome? DescribeSpare(LibraryItem library, string path, string name, string heldTitle, ISet<string> activeSourceKeys)
    {
        IReadOnlyList<string> files;
        try
        {
            files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
                : [path];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (files.Count == 0 || !files.All(ProcessorOutputReadiness.IsReady)
            || files.Any(file => activeSourceKeys.Contains(WorkPlanner.NormalizeSourceKey(file))))
        {
            return null;
        }

        return HealthCheckOutcome.Problem(
            $"processor-output:{library.Id}:{name.ToLowerInvariant()}",
            HealthCheckCategories.Processor,
            $"A second copy of {heldTitle} is waiting in the {library.Name} library's processor output folder",
            $"The {library.Name} library already has {heldTitle}, and a second copy is sitting in its processor output folder: {name}.",
            cause: $"A processor made more than one file for {heldTitle}, or it was fetched twice.",
            fix: $"Remove the spare copy of {heldTitle} ({name}), or keep it for now.",
            detailsJson: JsonSerializer.Serialize(new { libraryId = library.Id, path, kind = Directory.Exists(path) ? "folder" : "file", isMedia = true, spareCopyOf = heldTitle }));
    }

    private static HealthCheckOutcome? DescribeFolder(LibraryItem library, string path, string name, ISet<string> reservedSourceKeys)
    {
        IReadOnlyList<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // An empty folder is nothing to act on -- Import and Delete would both
        // be pointless -- so it is left alone rather than reported.
        if (files.Count == 0 || !files.All(ProcessorOutputReadiness.IsReady))
        {
            return null;
        }

        if (files.All(file => reservedSourceKeys.Contains(WorkPlanner.NormalizeSourceKey(file))))
        {
            return null;
        }

        var isMedia = files.Any(WorkPlanner.IsImportableVideoFile);
        return BuildOutcome(library, name, path, "folder", isMedia,
            string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase) && files.Count(WorkPlanner.IsImportableVideoFile) <= 1
                ? $"The {library.Name} library's processor output folder has a film folder Deluno cannot match to a hand-off: {name}."
                : $"The {library.Name} library's processor output folder has a season folder Deluno cannot match to a hand-off: {name}.");
    }

    private static HealthCheckOutcome? DescribeFile(LibraryItem library, string path, string name, ISet<string> reservedSourceKeys)
    {
        if (!ProcessorOutputReadiness.IsReady(path) || reservedSourceKeys.Contains(WorkPlanner.NormalizeSourceKey(path)))
        {
            return null;
        }

        var isMedia = WorkPlanner.IsImportableVideoFile(path);
        return BuildOutcome(library, name, path, isMedia ? "file" : "scraps", isMedia,
            isMedia
                ? $"The {library.Name} library's processor output folder has a file Deluno cannot match to a hand-off: {name}."
                : $"The {library.Name} library's processor output folder has files that are not media: {name}.");
    }

    private static HealthCheckOutcome BuildOutcome(LibraryItem library, string name, string path, string kind, bool isMedia, string message)
        => HealthCheckOutcome.Problem(
            $"processor-output:{library.Id}:{name.ToLowerInvariant()}",
            HealthCheckCategories.Processor,
            $"Unexpected files in the {library.Name} library's processor output folder",
            message,
            cause: $"{name} should not be in the {library.Name} library's processor output folder: it only occurs after something else already went wrong, or another app shares this folder.",
            fix: isMedia ? $"Import {name}, or clear it." : $"Clear {name}, or leave it for now.",
            detailsJson: JsonSerializer.Serialize(new { libraryId = library.Id, path, kind, isMedia }));
}

/// <summary>Matching a release or folder name to a film the library already holds (#1138): the title before the year, letters and digits only.</summary>
internal static class SpareCopy
{
    public static string? KeyOf(string title, int? year)
    {
        var letters = new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return letters.Length == 0 || year is null ? null : $"{letters}|{year}";
    }

    /// <summary>"Pulp.Fiction.1994.1080p.mkv" gives "pulpfiction|1994"; a name with no year gives null.</summary>
    public static string? TitleKey(string name)
    {
        var withoutExtension = WorkPlanner.IsImportableVideoFile(name) ? Path.GetFileNameWithoutExtension(name) : name;
        var tokens = withoutExtension.Split([' ', '.', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        var at = -1;
        for (var index = 1; index < tokens.Length; index++)
        {
            if (tokens[index].Length == 4 && int.TryParse(tokens[index], out var year) && year is >= 1900 and <= 2100)
            {
                at = index;
            }
        }

        return at < 1 ? null : KeyOf(string.Concat(tokens.Take(at)), int.Parse(tokens[at]));
    }
}
