namespace Deluno.Movies.Contracts;

public sealed record MovieListItem(
    string Id,
    string Title,
    int? ReleaseYear,
    string? ImdbId,
    bool Monitored,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
