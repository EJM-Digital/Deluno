namespace Deluno.Series.Contracts;

public sealed record EpisodeWorkflowDecision(
    string EpisodeId = "",
    string Decision = "",
    string? Reason = null,
    string? WantedStatus = null,
    bool IsReplacementAllowed = false,
    int? QualityDelta = null,
    string? CurrentQuality = null,
    string? TargetQuality = null);
