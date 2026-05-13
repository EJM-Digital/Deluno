namespace Deluno.Series.Contracts;

public sealed record SeriesUpcomingEpisodeItem(
    string SeriesId,
    string Title,
    int? StartYear,
    string? PosterUrl,
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string? EpisodeTitle,
    DateTimeOffset AirDateUtc);
