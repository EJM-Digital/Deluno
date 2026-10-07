namespace Deluno.Series.Contracts;

public sealed record SeriesInventoryDetail(
    string SeriesId,
    string Title,
    int? StartYear,
    int SeasonCount,
    int EpisodeCount,
    int ImportedEpisodeCount,
    IReadOnlyList<SeriesEpisodeInventoryItem> Episodes,
    SeriesNumberingDetail? Numbering = null)
{
    /// <summary>
    /// Seasons with at least one episode that is no longer monitored (removed by retention or by the person). A season
    /// pack for one of them would download those episodes again, so searches leave packs alone for these seasons (#1072).
    /// </summary>
    public IReadOnlyList<int> SeasonsWithRemovedEpisodes()
        => Episodes.Where(episode => !episode.Monitored && episode.SeasonNumber > 0).Select(episode => episode.SeasonNumber).Distinct().ToArray();

    /// <summary>
    /// The provider's titles for one season's episodes, by episode number, so a release that carries another episode's
    /// title can be refused for the one searched (#1173).
    /// </summary>
    public IReadOnlyDictionary<int, string> EpisodeTitlesForSeason(int seasonNumber)
        => Episodes
            .Where(episode => episode.SeasonNumber == seasonNumber && !string.IsNullOrWhiteSpace(episode.Title))
            .GroupBy(episode => episode.EpisodeNumber)
            .ToDictionary(group => group.Key, group => group.First().Title!.Trim());
}
