namespace Deluno.Jobs.Contracts;

public interface IDispatchAlertRepository
{
    Task<DispatchAlert> CreateAlertAsync(
        string dispatchId,
        string title,
        string summary,
        string alertKind,
        string severity,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchAlert>> GetOpenAlertsAsync(
        string? severityFilter = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAlertAsync(string alertId, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledges every open alert on one download, and returns how many.
    ///
    /// <para>Each alert is recorded beside an import-recovery case describing the
    /// same problem, and the case is what people see and dismiss. Dismissing the
    /// case left its alert open with no screen to close it, still counted by
    /// monitoring (#537), so settling the case settles the alert.</para>
    /// </summary>
    Task<int> AcknowledgeAlertsForDispatchAsync(string dispatchId, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledges the open alerts on every download of one release sent to one
    /// client, and returns how many: for a case recorded before it kept its
    /// download's id, which cannot say which of a release's grabs it was about.
    /// </summary>
    Task<int> AcknowledgeAlertsForReleaseAsync(string downloadClientId, string releaseName, CancellationToken cancellationToken);

    Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this dispatch already has an alert of this kind that has not
    /// been resolved - open, or acknowledged by a person.
    ///
    /// <para>The poller runs hourly and used to record the same failure every
    /// time it looked: a new alert, a new activity entry and a new recovery
    /// case each pass, for as long as the condition held. One alert per
    /// dispatch and kind is the marker that the failure is already known.</para>
    ///
    /// <para>Acknowledged counts. Asking only for open alerts meant a person who
    /// dismissed the problem got it back, with a new import issue, at the next
    /// poll - a download stuck for good was reported again every hour (seen on
    /// the rig, #537). A download that moves on resolves its alert; if it then
    /// sticks again, that is a new alert.</para>
    /// </summary>
    Task<bool> HasUnresolvedAlertAsync(string dispatchId, string alertKind, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every open alert whose download has moved past the problem it
    /// describes, and returns how many were closed.
    /// </summary>
    Task<int> ResolveSettledAlertsAsync(CancellationToken cancellationToken);
}
