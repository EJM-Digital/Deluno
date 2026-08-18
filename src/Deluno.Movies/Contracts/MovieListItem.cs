namespace Deluno.Movies.Contracts;

public sealed record MovieListItem(
    string Id,
    string Title,
    int? ReleaseYear,
    string? ImdbId,
    bool Monitored,
    bool HasFile,
    string? MetadataProvider,
    string? MetadataProviderId,
    string? OriginalTitle,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    IReadOnlyList<MetadataRatingItem> Ratings,
    string? Genres,
    string? ExternalUrl,
    string? MetadataJson,
    DateTimeOffset? MetadataUpdatedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    // A release year cannot say "in cinemas but not yet obtainable", which is
    // exactly the state that makes searching pointless. These do.
    DateOnly? InCinemasDate = null,
    DateOnly? DigitalReleaseDate = null,
    DateOnly? PhysicalReleaseDate = null,
    /// <summary>announced | inCinemas | released — when Deluno may start looking.</summary>
    string MinimumAvailability = "released",
    bool IsAvailable = true);
