namespace Deluno.Platform.Contracts;

/// <summary>
/// Instance-level progress for the guided first-run journey. This intentionally
/// contains no draft configuration or credentials; those remain in the form
/// until the user explicitly creates the baseline.
/// </summary>
public sealed record SetupProgressItem(
    int LastCompletedStep,
    bool IsSkipped,
    bool IsCompleted,
    DateTimeOffset UpdatedUtc);

public sealed record UpdateSetupProgressRequest(
    int LastCompletedStep,
    bool IsSkipped = false,
    bool IsCompleted = false);
