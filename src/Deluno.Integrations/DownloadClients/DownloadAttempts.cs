using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// One reading of "this release was sent to this client again" (#1182), shared by the queue's reading of a row and
/// the planner that hands finished downloads over, so the row never promises an import the planner will not make.
/// </summary>
public static class DownloadAttempts
{
    private static readonly string[] FileExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".nzb"];

    /// <summary>
    /// Whether two names are one release. A client names a single-file torrent after the file ("Film.2020.1080p-GRP.mkv")
    /// while the send Deluno recorded is named after the release ("Film.2020.1080p-GRP"), and a Usenet client names both the
    /// same way (rc.30.29: only the torrent clients' late reports were refused, #1242). Case and a trailing video or
    /// ".nzb" extension are ignored; nothing else is.
    /// </summary>
    public static bool SameRelease(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && string.Equals(WithoutExtension(left), WithoutExtension(right), StringComparison.OrdinalIgnoreCase);

    private static string WithoutExtension(string name)
    {
        var trimmed = name.Trim();
        foreach (var extension in FileExtensions)
        {
            if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && trimmed.Length > extension.Length)
            {
                return trimmed[..^extension.Length];
            }
        }

        return trimmed;
    }

    /// <summary>
    /// True when the latest send of this release to this client was made after <paramref name="sinceUtc"/>, the
    /// client took it, and it has not been imported or refused. An earlier send that was imported, or one that
    /// was refused, is not a new attempt waiting for its import.
    /// </summary>
    public static bool SentAgainSince(
        IReadOnlyList<DownloadDispatchItem> dispatches,
        string clientId,
        string releaseName,
        DateTimeOffset sinceUtc)
    {
        var latest = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, clientId, StringComparison.OrdinalIgnoreCase)
                && SameRelease(dispatch.ReleaseName, releaseName))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latest is not null
            && latest.CreatedUtc > sinceUtc
            && string.Equals(latest.GrabStatus, "succeeded", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(latest.ImportStatus);
    }

    /// <summary>
    /// True when an import job ended in failure (it failed or was dead-lettered) at or after <paramref name="sinceUtc"/>.
    /// A hand-off that was sent to the processor again after an older import failed is a new attempt and still waits;
    /// one whose own import failed is not waiting for anything (#1189), whichever screen is reading it.
    /// </summary>
    public static bool ImportJobFailedSince(JobQueueItem job, DateTimeOffset sinceUtc)
    {
        if (!string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var endedUtc = job.CompletedUtc ?? job.LastAttemptUtc ?? job.CreatedUtc;
        return endedUtc >= sinceUtc;
    }

    /// <summary>
    /// True when this hand-off's import ran to its end (#1182). A hand-off the processor finished stays Completed; a
    /// partial one can be settled Dismissed in the second before its import job completes, and it is the same thing:
    /// the import ran. A dismissed hand-off whose job did not complete (or is not known) is a closed problem, not an
    /// import that happened.
    /// </summary>
    public static bool ImportRan(ProcessorHandoffItem handoff, string? importJobStatus)
    {
        if (string.IsNullOrWhiteSpace(handoff.ImportJobId))
        {
            return false;
        }

        return handoff.Status == ProcessorHandoffStatuses.Completed
            || (handoff.Status == ProcessorHandoffStatuses.Dismissed
                && string.Equals(importJobStatus, "completed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The one list of job statuses that mean an import job has not finished yet (#1182): waiting for its turn,
    /// running, or put back to wait after a try. Transfers reads it as "Import queued" and the settler waits on it.
    /// </summary>
    public static bool ImportJobInFlight(string? importJobStatus)
        => importJobStatus is not null
           && (string.Equals(importJobStatus, "queued", StringComparison.OrdinalIgnoreCase)
               || string.Equals(importJobStatus, "running", StringComparison.OrdinalIgnoreCase)
               || string.Equals(importJobStatus, "requeued", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when this hand-off ended because its download was gone and was then dismissed (#1200). It is closed
    /// history, never "ready to import": the files it would import are not there. A release sent again since is a
    /// new attempt and not covered by it.
    /// </summary>
    public static bool SourceGoneClosed(ProcessorHandoffItem handoff)
        => handoff.Status == ProcessorHandoffStatuses.Dismissed
           && string.IsNullOrWhiteSpace(handoff.ImportJobId)
           && ProcessorFailureClasses.IsSourceGoneMessage(handoff.FailureMessage);
}
