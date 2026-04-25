namespace Deluno.Movies.Contracts;

public sealed record CreateMovieRequest(
    string? Title,
    int? ReleaseYear,
    string? ImdbId,
    bool Monitored = true,
    string? MetadataProvider = null,
    string? MetadataProviderId = null,
    string? OriginalTitle = null,
    string? Overview = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    double? Rating = null,
    string? Genres = null,
    string? ExternalUrl = null,
    string? MetadataJson = null);
