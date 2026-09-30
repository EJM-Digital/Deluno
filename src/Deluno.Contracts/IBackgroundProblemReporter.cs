namespace Deluno.Contracts;

/// <summary>
/// How a background job says "something I do keeps failing" so Health shows it
/// in plain words (#1080), without the job having to stop. A problem stays until
/// the same key is cleared (the job succeeded again) or Deluno restarts.
/// </summary>
public interface IBackgroundProblemReporter
{
    /// <summary>Shows or updates one problem. <paramref name="key"/> is stable per problem (for example "lane:import").</summary>
    void Report(string key, string title, string message, string reason);

    /// <summary>Removes the problem for <paramref name="key"/>, if it is showing.</summary>
    void Clear(string key);
}

/// <summary>
/// A start-up step that failed and can be run again on request, so its Health row has a
/// "Try again" button instead of only a Snooze (#1080). The step reports or clears its own
/// problem when it runs.
/// </summary>
public interface IBackgroundProblemRetry
{
    /// <summary>The problem key this step reports under (for example "startup:collection-exclusions").</summary>
    string ProblemKey { get; }

    /// <summary>Runs the step again now. Returns true when it finished and its problem is cleared; never throws.</summary>
    Task<bool> RetryAsync(CancellationToken cancellationToken);
}
