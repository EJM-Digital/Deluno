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

    Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this dispatch already has an open alert of this kind.
    ///
    /// <para>The poller runs hourly and used to record the same failure every
    /// time it looked: a new alert, a new activity entry and a new recovery
    /// case each pass, for as long as the condition held. One open alert per
    /// dispatch and kind is the marker that the failure is already being
    /// handled.</para>
    /// </summary>
    Task<bool> HasOpenAlertAsync(string dispatchId, string alertKind, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every open alert whose download has moved past the problem it
    /// describes, and returns how many were closed.
    /// </summary>
    Task<int> ResolveSettledAlertsAsync(CancellationToken cancellationToken);
}
