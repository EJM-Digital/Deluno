namespace Deluno.Series.Contracts;

public sealed record SeriesEpisodeInventoryItem(
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
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
    DateTimeOffset UpdatedUtc,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    DateOnly? AirDate = null,
    string? NumberingSource = null,
    string? CatalogueSource = null,
    /// <summary>What this episode's own last search came to, in words: what was sent, or why nothing was (#1159).</summary>
    string? LastSearchResult = null);
