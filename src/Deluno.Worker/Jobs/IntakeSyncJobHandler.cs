using Deluno.Jobs.Contracts;
using Deluno.Worker.Intake;

namespace Deluno.Worker.Jobs;

public sealed class IntakeSyncJobHandler(IIntakeSyncService intakeSyncService) : IJobHandler
{
    public string JobType => "intake.sync";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseIntakeSyncPayload(job.PayloadJson);
        var sourceId = payload?.SourceId ?? job.RelatedEntityId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return "Skipped intake sync because no source id was provided.";
        }

        var result = await intakeSyncService.RunAsync(sourceId, job.Id, payload?.Manual == true, cancellationToken);
        return $"Intake sync completed for {result.SourceName}: {result.Summary}";
    }
}
