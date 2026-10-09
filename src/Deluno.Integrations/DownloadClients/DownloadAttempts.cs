using Deluno.Jobs.Contracts;

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
}
