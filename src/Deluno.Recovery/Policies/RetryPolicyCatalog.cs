using Deluno.Contracts;

namespace Deluno.Recovery.Policies;

public sealed class RetryPolicyCatalog : IRetryPolicyCatalog
{
    public RetryPolicy GrabTimeout { get; } = new(
        FailureKind: "grab-timeout",
        MaxRetries: 3,
        InitialDelay: TimeSpan.FromMinutes(30),
        BackoffMultiplier: 2.0,
        MaxDelay: TimeSpan.FromHours(4));

    private RetryPolicy DetectionTimeout { get; } = new(
        FailureKind: "detection-timeout",
        MaxRetries: 2,
        InitialDelay: TimeSpan.FromHours(1),
        BackoffMultiplier: 2.0,
        MaxDelay: TimeSpan.FromHours(6));

    private RetryPolicy ImportFailed { get; } = new(
        FailureKind: "import-failed",
        MaxRetries: 1,
        InitialDelay: TimeSpan.FromHours(6),
        BackoffMultiplier: 1.0,
        MaxDelay: TimeSpan.FromHours(6));

    public RetryPolicy GetPolicyForKind(string failureKind) => failureKind switch
    {
        "grab-timeout" => GrabTimeout,
        "detection-timeout" => DetectionTimeout,
        "import-failed" => ImportFailed,
        _ => new RetryPolicy(failureKind, MaxRetries: 0, InitialDelay: TimeSpan.Zero, BackoffMultiplier: 1.0, MaxDelay: TimeSpan.Zero)
    };

    public TimeSpan CalculateNextRetryDelay(int attemptNumber, RetryPolicy policy)
    {
        if (attemptNumber <= 0 || attemptNumber > policy.MaxRetries)
            return TimeSpan.Zero;

        var exponentialDelay = TimeSpan.FromMilliseconds(
            policy.InitialDelay.TotalMilliseconds * Math.Pow(policy.BackoffMultiplier, attemptNumber - 1));

        return exponentialDelay > policy.MaxDelay ? policy.MaxDelay : exponentialDelay;
    }
}
