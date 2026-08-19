using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

public sealed class FilesystemImportExecuteJobHandler(IImportPipelineService importPipelineService) : IJobHandler
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
            throw new InvalidOperationException(result.Message);
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
