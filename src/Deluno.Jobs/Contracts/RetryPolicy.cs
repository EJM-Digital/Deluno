namespace Deluno.Jobs.Contracts;

public sealed record RetryPolicy(
    string FailureKind,
    int MaxRetries,
    TimeSpan InitialDelay,
    double BackoffMultiplier,
    TimeSpan MaxDelay);

public static class RetryPolicies
{
    public static readonly RetryPolicy GrabTimeout = new(
        FailureKind: "grab-timeout",
        MaxRetries: 3,
        InitialDelay: TimeSpan.FromMinutes(30),
        BackoffMultiplier: 2.0,
        MaxDelay: TimeSpan.FromHours(4));

    public static readonly RetryPolicy DetectionTimeout = new(
        FailureKind: "detection-timeout",
        MaxRetries: 2,
        InitialDelay: TimeSpan.FromHours(1),
        BackoffMultiplier: 2.0,
        MaxDelay: TimeSpan.FromHours(6));

    public static readonly RetryPolicy ImportFailed = new(
        FailureKind: "import-failed",
        MaxRetries: 1,
        InitialDelay: TimeSpan.FromHours(6),
        BackoffMultiplier: 1.0,
        MaxDelay: TimeSpan.FromHours(6));

    public static RetryPolicy GetPolicyForKind(string failureKind) => failureKind switch
    {
        "grab-timeout" => GrabTimeout,
        "detection-timeout" => DetectionTimeout,
        "import-failed" => ImportFailed,
        _ => new RetryPolicy(failureKind, MaxRetries: 0, InitialDelay: TimeSpan.Zero, BackoffMultiplier: 1.0, MaxDelay: TimeSpan.Zero)
    };

    public static TimeSpan CalculateNextRetryDelay(int attemptNumber, RetryPolicy policy)
    {
        if (attemptNumber <= 0 || attemptNumber > policy.MaxRetries)
            return TimeSpan.Zero;

        var exponentialDelay = TimeSpan.FromMilliseconds(
            policy.InitialDelay.TotalMilliseconds * Math.Pow(policy.BackoffMultiplier, attemptNumber - 1));

        return exponentialDelay > policy.MaxDelay ? policy.MaxDelay : exponentialDelay;
    }
}
