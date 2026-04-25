namespace Deluno.Series.Contracts;

public sealed record SeriesInventorySummary(
    int SeriesCount,
    int SeasonCount,
    int EpisodeCount,
    int ImportedEpisodeCount);
