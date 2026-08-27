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
    double? ApproximateBitrateMbps = null,
    /// <summary>
    /// The search state Deluno holds for this movie, from the paged catalogue
    /// query's join to the wanted state.
    ///
    /// The grid used to read these from <c>/api/movies/wanted</c>, whose
    /// <c>recentItems</c> is capped at 25 — so in a library of any size, most
    /// cards had no status, no reason and no target quality at all and fell back
    /// to "is there a file". A page carries its own, however deep it is.
    ///
    /// Null throughout means Deluno is not tracking the movie in any library, so
    /// there is no state to report — which is not the same as a state of "no".
    /// </summary>
    string? LibraryId = null,
    string? WantedStatus = null,
    string? WantedReason = null,
    string? TargetQuality = null,
    bool? QualityCutoffMet = null,
    DateTimeOffset? LastSearchUtc = null,
    DateTimeOffset? NextEligibleSearchUtc = null,
    /// <summary>
    /// The bar under the poster: the subtitle languages you asked for.
    ///
    /// DESIGN-001 gives every title a bar, and it is subtitle
    /// languages, proportioned to what you asked for. Two languages and you have
    /// English is half green. Zero means no languages were asked for, and a
    /// title with none draws no bar at all — the bar is painted over the poster
    /// and takes no layout space, so there is nothing to hold a place for.
    ///
    /// A movie is one file, so <c>Wanted</c> is simply the languages asked for.
    ///
    /// Zero until Subber exists. The contract is here now so the mark does not
    /// have to be redesigned around it later — which is the whole reason the
    /// design settled the vocabulary before the feature.
    /// </summary>
    int SubtitleLanguagesWanted = 0,
    int SubtitleLanguagesHeld = 0);
