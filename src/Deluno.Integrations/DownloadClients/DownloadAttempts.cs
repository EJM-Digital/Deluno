using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// One reading of "this release was sent to this client again" (#1182), shared by the queue's reading of a row and
/// the planner that hands finished downloads over, so the row never promises an import the planner will not make.
/// </summary>
public static class DownloadAttempts
{
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
                && string.Equals(dispatch.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latest is not null
            && latest.CreatedUtc > sinceUtc
            && string.Equals(latest.GrabStatus, "succeeded", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(latest.ImportStatus);
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
}
