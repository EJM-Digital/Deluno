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
    string? MetadataJson = null,
    string? LibraryId = null,
    /// <summary>Search for this film right away if it comes out missing (#793). The only thing that makes an add search at once: Monitored alone does not (#1145).</summary>
    bool SearchForMissingNow = false,
    /// <summary>Search for this film right away if it comes out cutoff-unmet (#793). Like <see cref="SearchForMissingNow"/>, an explicit choice.</summary>
    bool SearchForCutoffUnmetNow = false,
    /// <summary>Tag names picked in the add dialog (#1038); written with the title before routing and the added notification.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>The quality profile this film is judged by, chosen in the add dialog (#1145). Null follows the library's profile.</summary>
    string? QualityProfileId = null);
