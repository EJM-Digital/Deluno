namespace Deluno.Series.Contracts;

/// <summary>
/// One season, of one series, as the cross-series Season Pass screen (#791)
/// shows it: how many episodes it has and how many of them are monitored.
/// The screen needs no more than this per row - the series/season grid is
/// built entirely client-side from a flat list of these.
/// </summary>
public sealed record SeriesSeasonPassItem(
    string SeriesId,
    string SeriesTitle,
    int SeasonNumber,
    int EpisodeCount,
    int MonitoredCount);
