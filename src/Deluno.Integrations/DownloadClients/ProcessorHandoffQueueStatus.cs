using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Where a handed-off download has actually got to, told from its hand-off
/// record and the import job that record produced.
///
/// This exists because path matching cannot answer the question. A library that
/// refines before importing imports from the processor output path, while the
/// same item sits in the download client queue under its original download
/// path, so the import job and the queue item never share a key. The hand-off
/// is the only record holding both. Reading it here is what lets a finished item
/// leave the Processing stage; without it a successful import still reported
/// processingCount 1 for ever (#280).
/// </summary>
public static class ProcessorHandoffQueueStatus
{
    public static string Resolve(
        ProcessorHandoffItem handoff,
        IReadOnlyDictionary<string, JobQueueItem> jobsById,
        string fallback)
    {
        var status = handoff.Status?.ToLowerInvariant();
        switch (status)
        {
            case ProcessorHandoffStatuses.Waiting:
            case ProcessorHandoffStatuses.Submitted:
            case ProcessorHandoffStatuses.Accepted:
            case ProcessorHandoffStatuses.Started:
                // The processor still has it.
                return DownloadQueueStatuses.WaitingForProcessor;

            case ProcessorHandoffStatuses.Released:
                // The original is going through the ordinary import, so the
                // client's own reading of the download says where it is.
                return fallback;

            case ProcessorHandoffStatuses.Completed:
                break;

            default:
                // failed, rejected, timed-out, stalled, lost, cancelled: nothing
                // is coming back and a person chooses what happens next. These
                // read "waiting for the cleaned output" for ever, beside a
                // hand-off already called lost (seen on the rig).
                return DownloadQueueStatuses.ProcessingFailed;
        }

        if (string.IsNullOrWhiteSpace(handoff.ImportJobId))
        {
            return DownloadQueueStatuses.Processed;
        }

        // The job list is a recent window. A hand-off whose import job has aged
        // out of it is finished work, not work still in flight, so it must not
        // fall back to a Processing status and pin the stage open again.
        return jobsById.TryGetValue(handoff.ImportJobId, out var importJob)
            ? importJob.Status switch
            {
                "queued" or "running" => DownloadQueueStatuses.ImportQueued,
                "completed" => DownloadQueueStatuses.Imported,
                "failed" => DownloadQueueStatuses.ImportFailed,
                _ => fallback
            }
            : DownloadQueueStatuses.Imported;
    }
}
