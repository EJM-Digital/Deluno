namespace Deluno.Series.Contracts;

public sealed record SeriesInventoryDetail(
    string SeriesId,
    string Title,
    int? StartYear,
    int SeasonCount,
    int EpisodeCount,
    int ImportedEpisodeCount,
    IReadOnlyList<SeriesEpisodeInventoryItem> Episodes);
