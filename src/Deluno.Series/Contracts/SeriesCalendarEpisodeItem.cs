namespace Deluno.Series.Contracts;

/// <summary>An episode placed on a date, with just enough to draw a calendar row.</summary>
public sealed record SeriesCalendarEpisodeItem(
    string EpisodeId,
    string SeriesId,
    string SeriesTitle,
    string? PosterUrl,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset AirDateUtc,
    bool HasFile,
    bool Monitored,
    string WantedStatus);
