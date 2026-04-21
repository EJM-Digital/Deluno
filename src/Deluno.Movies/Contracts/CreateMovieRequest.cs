namespace Deluno.Movies.Contracts;

public sealed record CreateMovieRequest(
    string? Title,
    int? ReleaseYear,
    string? ImdbId,
    bool Monitored = true);
