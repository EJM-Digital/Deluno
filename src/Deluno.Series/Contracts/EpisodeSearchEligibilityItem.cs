namespace Deluno.Series.Contracts;

public sealed record EpisodeSearchEligibilityItem(
    string EpisodeId,
    string SeriesId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc);
