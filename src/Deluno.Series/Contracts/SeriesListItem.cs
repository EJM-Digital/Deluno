namespace Deluno.Series.Contracts;

public sealed record SeriesListItem(
    string Id,
    string Title,
    int? StartYear,
    string? ImdbId,
    bool Monitored,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
