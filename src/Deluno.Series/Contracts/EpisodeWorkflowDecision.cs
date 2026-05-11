namespace Deluno.Series.Contracts;

public sealed record EpisodeWorkflowDecision(
    string EpisodeId,
    string Decision,
    string? Reason);
