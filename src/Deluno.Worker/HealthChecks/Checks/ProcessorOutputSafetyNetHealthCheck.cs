using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
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
/// listed. Nothing is ever deleted by this check -- see
/// <c>ExternalIntegrationEndpointRouteBuilderExtensions</c>'s
/// <c>/processors/output/delete</c> for the only way that happens, and only on
/// request.</para>
/// </summary>
public sealed class ProcessorOutputSafetyNetHealthCheck(
    ILibrariesRepository libraries,
    IProcessorRepository processors,
    IJobQueueRepository jobQueueRepository) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    /// <summary>Same batch size <see cref="WorkPlanner"/> reads existing jobs with -- one list, read once.</summary>
    private const int MaintenancePlanningBatchSize = 600;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
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

        var allHandoffs = await processors.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.AcceptingLateOutput, cancellationToken);

        var outcomes = new List<HealthCheckOutcome>();
        foreach (var library in refineLibraries)
        {
            var root = library.ProcessorOutputPath!;
            if (!Directory.Exists(root))
            {
                continue;
            }

            var activeNames = allHandoffs
                .Where(handoff => string.Equals(handoff.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase))
                .SelectMany(handoff => WorkPlanner.ExpectedOutputDirectoryNames(handoff.SourcePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                if (string.IsNullOrWhiteSpace(name) || activeNames.Contains(name))
                {
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
            $"{library.Name}'s processor output folder has a season folder Deluno cannot match to a hand-off: {name}.");
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
                ? $"{library.Name}'s processor output folder has a file Deluno cannot match to a hand-off: {name}."
                : $"{library.Name}'s processor output folder has files that are not media: {name}.");
    }

    private static HealthCheckOutcome BuildOutcome(LibraryItem library, string name, string path, string kind, bool isMedia, string message)
        => HealthCheckOutcome.Problem(
            $"processor-output:{library.Id}:{name.ToLowerInvariant()}",
            HealthCheckCategories.Processor,
            $"Unexpected files in {library.Name}'s processor output folder",
            message,
            cause: "This shouldn't happen: it only occurs after something else already went wrong, or another app shares this folder.",
            fix: isMedia ? "Import it, or delete it." : "Delete it, or leave it for now.",
            detailsJson: JsonSerializer.Serialize(new { libraryId = library.Id, path, kind, isMedia }));
}
