namespace Deluno.Series.Contracts;

public sealed record SeriesEpisodeInventoryItem(
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? AirDateUtc,
    bool Monitored,
    bool HasFile,
    string WantedStatus,
    string WantedReason,
    bool QualityCutoffMet,
    string? CurrentQuality,
    string? TargetQuality,
    bool PreventLowerQualityReplacements,
    int? LastQualityDeltaDecision,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc,
    DateTimeOffset UpdatedUtc);
