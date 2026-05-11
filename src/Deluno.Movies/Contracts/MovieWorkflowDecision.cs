namespace Deluno.Movies.Contracts;

public sealed record MovieWorkflowDecision(
    string WantedStatus,
    string Reason,
    bool IsReplacementAllowed,
    int? QualityDelta,
    string? CurrentQuality,
    string? TargetQuality);
