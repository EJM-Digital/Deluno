namespace Deluno.Contracts;

/// <summary>What happened to one check between its last run and this one (#909).</summary>
public enum HealthCheckTransition
{
    /// <summary>No change worth telling anyone about.</summary>
    Unchanged,

    /// <summary>It was fine, or unseen, and now it is a problem. Notify.</summary>
    Started,

    /// <summary>It was a problem, and now it is not. Notify.</summary>
    Cleared
}

/// <summary>
/// What one check row did. <paramref name="ScreenChanged"/> is true when what a screen shows moved even though no problem
/// started or cleared (an unknown row turning fine, a message changing, a new row), so a screen reads it again (#1229).
/// </summary>
public sealed record HealthCheckTransitionResult(HealthCheckResultItem Result, HealthCheckTransition Transition, bool ScreenChanged = false);

/// <summary>
/// The one store every health-check surface reads (#909): System &#8594;
/// Health, the Dashboard tile, the bell and outbound notifications. A check
/// writes here once and every surface agrees, because there is nowhere else
/// to disagree from.
/// </summary>
public interface IHealthCheckRepository
{
    Task<IReadOnlyList<HealthCheckTransitionResult>> UpsertResultsAsync(
        IReadOnlyList<HealthCheckOutcome> outcomes,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HealthCheckResultItem>> ListAsync(CancellationToken cancellationToken);

    Task<HealthCheckResultItem?> GetAsync(string id, CancellationToken cancellationToken);

    Task<HealthCheckSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<HealthCheckResultItem?> SnoozeAsync(string id, DateTimeOffset? untilUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a check's row entirely -- used when the thing it was about
    /// (a deleted download client, a removed library) no longer exists, so a
    /// stale problem does not outlive what it described.
    /// </summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the checks for one category to run before the next scheduled
    /// pass, because a setting they depend on just changed (#909).
    /// </summary>
    Task RequestImmediateRunAsync(string category, CancellationToken cancellationToken);

    /// <summary>Every category asked for since the last time this was called, and clears the request.</summary>
    Task<IReadOnlyList<string>> TakePendingImmediateRunsAsync(CancellationToken cancellationToken);
}
