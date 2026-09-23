using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Worker.Services;

namespace Deluno.Worker.Jobs;

public sealed class FilesystemImportExecuteJobHandler(
    IImportPipelineService importPipelineService,
    ProcessorOutcomeReporter? processorOutcomes = null) : IJobHandler
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
