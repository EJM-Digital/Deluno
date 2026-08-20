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
    bool IsAvailable = true,
    /// <summary>
    /// Size of the file Deluno is tracking, and the quality it detected.
    ///
    /// Populated by the paged catalogue query only. The list has always shown a
    /// size column and a size sort, but read them from a metadata blob field no
    /// provider writes — these come from the wanted state, where Deluno actually
    /// records them.
    /// </summary>
    long? FileSizeBytes = null,
    string? CurrentQuality = null,
    /// <summary>
    /// What the file is, and what the title is.
    ///
    /// Populated by the paged catalogue query. The list has always shown a size
    /// column, a codec column and sorts for runtime, popularity and votes, and
    /// read every one of them from a metadata blob that never carried them. The
    /// file-shaped ones come from the file name, the title-shaped ones from the
    /// provider, and the bitrate from dividing one by the other — which is why
    /// it says "approximate" and is not stored.
    /// </summary>
    string? FilePath = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? AudioChannels = null,
    string? ReleaseGroup = null,
    int? RuntimeMinutes = null,
    double? Popularity = null,
    int? VoteCount = null,
    double? ApproximateBitrateMbps = null);
