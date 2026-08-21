namespace Deluno.Contracts;

/// <summary>One retry policy exposed to the mechanisms that schedule recovery work.</summary>
public sealed record RetryPolicy(
    string FailureKind,
    int MaxRetries,
    TimeSpan InitialDelay,
    double BackoffMultiplier,
    TimeSpan MaxDelay);

/// <summary>
/// Keeps retry policy ownership in the recovery module while allowing jobs and
/// integrations to schedule work without referencing that module.
/// </summary>
public interface IRetryPolicyCatalog
{
    RetryPolicy GrabTimeout { get; }

    RetryPolicy GetPolicyForKind(string failureKind);

    TimeSpan CalculateNextRetryDelay(int attemptNumber, RetryPolicy policy);
}
