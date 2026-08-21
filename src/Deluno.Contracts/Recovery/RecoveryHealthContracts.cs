namespace Deluno.Contracts;

/// <summary>
/// The small, normalized snapshot needed to evaluate download health. Keeping this
/// contract outside the integration adapters lets recovery remain independent of
/// any particular download-client protocol.
/// </summary>
public sealed record RecoveryQueueSnapshot(
    string Status,
    string? ErrorMessage,
    string? SourcePath,
    double SpeedMbps,
    DateTimeOffset AddedUtc,
    int EtaSeconds,
    string ReleaseName);

public sealed record RecoveryHealthFinding(
    string Severity,
    string Kind,
    string Summary,
    string Evidence,
    string RecommendedAction,
    bool CanSafelyRetry,
    bool CanSafelyRemove);

public interface IRecoveryHealthEvaluator
{
    IReadOnlyList<RecoveryHealthFinding> Evaluate(
        RecoveryQueueSnapshot item,
        DateTimeOffset capturedUtc);
}
