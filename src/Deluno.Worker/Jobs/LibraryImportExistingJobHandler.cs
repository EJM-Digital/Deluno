using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Advances one existing-library import by a slice, then queues the next slice
/// if there is more to do.
///
/// Slicing is what keeps a multi-hour import inside the job system rather than
/// alongside it. A handler that ran the whole import would hold its lease for
/// hours, look like a stalled worker, and take the whole import down with it if
/// the process restarted. Each slice instead finishes well inside the lease and
/// commits its position, so a restart costs at most one slice.
/// </summary>
public sealed class LibraryImportExistingJobHandler(
    IExistingLibraryImportService importService,
    IJobScheduler jobScheduler)
    : IJobHandler
{
    public string JobType => "library.import.existing";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RunId))
        {
            throw new InvalidOperationException("Library import job payload could not be read.");
        }

        var outcome = await importService.RunSliceAsync(payload.RunId, cancellationToken);

        if (outcome.MoreWorkRemains)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: JobType,
                    Source: "library-import",
                    PayloadJson: job.PayloadJson,
                    RelatedEntityType: job.RelatedEntityType,
                    RelatedEntityId: job.RelatedEntityId,
                    DedupeKey: LibraryImportSliceOutcome.ContinuationDedupeKey(payload.RunId, outcome.ProcessedTotal)),
                cancellationToken);
        }

        return outcome.Message;
    }

    private static LibraryImportSlicePayload? ParsePayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryImportSlicePayload>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LibraryImportSlicePayload(string RunId, string LibraryId);
}
