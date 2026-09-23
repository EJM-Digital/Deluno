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

            throw new InvalidOperationException(result.Message);
        }

        if (payload.HandoffId is { Length: > 0 } imported && processorOutcomes is not null)
        {
            await processorOutcomes.ReportAsync(
                imported,
                ProcessorHandoffOutcome.Imported,
                result.Response?.Preview.DestinationPath,
                null,
                cancellationToken);
        }

        return result.Response?.Message ?? "Import completed.";
    }

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
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
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
