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
                // The processor still has it, unless the import for this very hand-off has already ended in
                // failure: the output Deluno was told about was gone, and "waiting" would disagree with the
                // Needs action entry beside it (#1189).
                return !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                    && jobsById.TryGetValue(handoff.ImportJobId, out var failedImport)
                    && Deluno.Integrations.DownloadClients.DownloadAttempts.ImportJobFailedSince(failedImport, handoff.UpdatedUtc)
                        ? DownloadQueueStatuses.ImportFailed
                        : DownloadQueueStatuses.WaitingForProcessor;

            case ProcessorHandoffStatuses.Dismissed:
                // Closed on purpose, by a person or because the title was imported another way.
                // It is history, never a problem: the client's own reading says where it is. Except when its import
                // ran to its end (a partial result Deluno imported by its main video, #1182): that download is in
                // the library, not "Ready to import".
                return !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                    && jobsById.TryGetValue(handoff.ImportJobId, out var dismissedImport)
                    && dismissedImport.Status == "completed"
                        ? DownloadQueueStatuses.Imported
                        : fallback;

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
                "failed" or "dead-letter" => DownloadQueueStatuses.ImportFailed,
                _ => fallback
            }
            : DownloadQueueStatuses.Imported;
    }
}
