namespace Deluno.Series.Contracts;

/// <summary>
/// An episode Deluno still wants, across every series, with enough of its show
/// to render a row. One query: the page used to fetch each series' whole
/// inventory in turn, which after a catalogue sync is thousands of rows.
/// </summary>
public sealed record WantedEpisodeItem(
    string EpisodeId,
    string SeriesId,
    string SeriesTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? AirDateUtc,
    bool Monitored,
    string WantedStatus,
    string WantedReason,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc);
