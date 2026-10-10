using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Worker.Services;

namespace Deluno.Worker.Jobs;

public sealed class FilesystemImportExecuteJobHandler(
    IImportPipelineService importPipelineService,
    ProcessorOutcomeReporter? processorOutcomes = null,
    IDownloadDispatchesRepository? dispatches = null,
    IBlockedReleaseRepository? blockedReleases = null,
    IJobScheduler? jobScheduler = null,
    ILibrariesRepository? libraries = null) : IJobHandler
{
    public string JobType => "filesystem.import.execute";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParseImportPayload(job.PayloadJson);
        if (payload is null)
        {
            throw new InvalidOperationException("Import job payload could not be read.");
        }

        var result = await importPipelineService.ExecuteAsync(payload, cancellationToken);
        if (!result.Succeeded)
        {
            // Only when Deluno is finished trying. A processor takes "not
            // imported" as permission to remove its copy, and a retry of a
            // transient failure would then find nothing left to import (#578).
            if (payload.HandoffId is { Length: > 0 } handoff &&
                processorOutcomes is not null &&
                ProcessorOutcomeReporter.WillNotTryAgain(result, job))
            {
                await processorOutcomes.ReportAsync(
                    handoff,
                    ProcessorHandoffOutcome.NotImported,
                    null,
                    result.Message,
                    cancellationToken);
            }

            // A refusal is final on the first attempt (#603): a second reads the
            // same bytes. It is recorded - a recovery case and, for the file
            // itself, the blocklist - so the job ends here rather than failing
            // two more times and being counted a third time as a failed job.
            if (ProcessorOutcomeReporter.IsRefusal(result))
            {
                return await SearchForAnotherCopyAsync(payload, cancellationToken)
                    ? $"Not imported: {result.Message} Deluno is searching for another copy."
                    : $"Not imported: {result.Message}";
            }

            // A source file that has not settled yet - one file, or a whole
            // season pack that shares its settle window (#652) - is not a
            // broken import: it is retried after a short wait without
            // spending one of the pack's dead-letter attempts, the same way
            // an indexer pacing pass defers a search (#598).
            if (result.StatusCode == ImportFileReadiness.RetryableStatusCode)
            {
                throw new JobDeferredException(ImportFileReadiness.SettleWindow, result.Message);
            }

            // The import already says why in plain words; the job keeps that, not a generic line (#1167).
            throw new PlainFailureException(result.Message);
        }

        if (payload.HandoffId is { Length: > 0 } imported && processorOutcomes is not null)
        {
            // The hand-off holding the processor's source path now settles from this import, whichever id the job
            // was queued under: a release sent again to the same client has a new id, and the processor answers
            // for it, but the file that was imported is the one both sends were about (#1226).
            var holder = payload.Preview.LibraryId is { Length: > 0 } libraryId
                ? await processorOutcomes.SettleFromImportAsync(libraryId, payload.ProcessedFromPath, payload.Preview.SourcePath, job.Id, cancellationToken)
                : null;
            var destination = result.Response?.Preview.DestinationPath;
            var skipped = DescribeSkippedFiles(result.Response?.SkippedFiles);
            await processorOutcomes.ReportAsync(imported, ProcessorHandoffOutcome.Imported, destination, skipped, cancellationToken);
            if (holder is { Status: ProcessorHandoffStatuses.Completed } && !string.Equals(holder.Id, imported, StringComparison.OrdinalIgnoreCase))
            {
                await processorOutcomes.ReportAsync(holder.Id, ProcessorHandoffOutcome.Imported, destination, skipped, cancellationToken);
            }
        }

        return result.Response?.Message ?? "Import completed.";
    }

    /// <summary>
    /// Plain words for the pack files a season-pack import left alone, so the
    /// processor outcome report says which files were skipped and why (#652),
    /// not only that the pack, as a whole, was imported.
    /// </summary>
    private static string? DescribeSkippedFiles(IReadOnlyList<ImportPackSkippedFile>? skippedFiles)
        => skippedFiles is not { Count: > 0 }
            ? null
            : $"Skipped {skippedFiles.Count} file(s) that do not resolve to a canonical episode: {string.Join(", ", skippedFiles.Select(file => Path.GetFileName(file.SourcePath)))}.";

    /// <summary>
    /// When the refusal put the release on the blocklist, look for another copy
    /// of the title now (#604) - the same search a processor's refusal queues.
    /// A refusal that blocked nothing, a destination conflict say, queues none.
    /// </summary>
    private async Task<bool> SearchForAnotherCopyAsync(ImportExecuteRequest payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.DispatchId)
            || dispatches is null || blockedReleases is null || jobScheduler is null)
        {
            return false;
        }

        var dispatch = await dispatches.GetDispatchAsync(payload.DispatchId, cancellationToken);
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId) || !DispatchTargets.HasTitle(dispatch.EntityType))
        {
            return false;
        }

        var refused = (await blockedReleases.ListForAsync(dispatch.MediaType, dispatch.EntityId, cancellationToken))
            .Any(block => string.Equals(block.State, BlockedReleaseStates.Refused, StringComparison.OrdinalIgnoreCase)
                && string.Equals(block.ReleaseName, dispatch.ReleaseName, StringComparison.OrdinalIgnoreCase));
        if (!refused)
        {
            return false;
        }

        var libraryName = libraries is null
            ? dispatch.MediaType
            : (await libraries.ListLibrariesAsync(cancellationToken)).FirstOrDefault(library => library.Id == dispatch.LibraryId)?.Name ?? dispatch.MediaType;
        await AnotherCopySearch.EnqueueAsync(jobScheduler, dispatch, libraryName, "import-refused", dispatch.Id, cancellationToken);
        return true;
    }

    private static ImportExecuteRequest? ParseImportPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportExecuteRequest>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch
        {
            return null;
        }
    }
}
