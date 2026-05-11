namespace Deluno.Series.Contracts;

public sealed record EpisodeWorkflowDecision(
    string EpisodeId,
    string Decision,
    string? Reason,
    string? WantedStatus,
    bool IsReplacementAllowed,
    int? QualityDelta,
    string? CurrentQuality,
    string? TargetQuality);
