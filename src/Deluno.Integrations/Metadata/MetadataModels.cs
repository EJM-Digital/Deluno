namespace Deluno.Integrations.Metadata;

public sealed record MetadataSearchResult(
    string Provider,
    string ProviderId,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    IReadOnlyList<MetadataRatingItem> Ratings,
    IReadOnlyList<string> Genres,
    string? ImdbId,
    string? ExternalUrl,
    IReadOnlyList<MetadataCastMember>? Cast = null,
    // Movies carry several dates and they mean different things: a movie can be
    // in cinemas months before it is obtainable. `Year` alone cannot express
    // "not out yet", which is what an availability rule needs to decide.
    DateOnly? InCinemasDate = null,
    DateOnly? DigitalReleaseDate = null,
    DateOnly? PhysicalReleaseDate = null,
    /// <summary>
    /// Runtime, popularity and vote count.
    ///
    /// The library list has always offered sorting on all three and never had
    /// any of them: they were read from a metadata blob that did not carry
    /// them, so every title compared as zero. Runtime earns its place twice
    /// over — it is also the denominator that turns a file size into a bitrate.
    /// </summary>
    int? RuntimeMinutes = null,
    double? Popularity = null,
    int? VoteCount = null,
    /// <summary>
    /// The rest of what a provider detail lookup already carries, and Deluno
    /// had never asked it for.
    ///
    /// Three of these are not new ideas: the library adapters read
    /// <c>certification</c>, <c>collection</c> and <c>language</c> straight out
    /// of the stored metadata blob and have done for a long time, and nothing
    /// ever put a value in any of them — so they read empty on every install,
    /// the same shape as the codec and release-group columns the list displayed
    /// for months with nothing populating them.
    ///
    /// The rest are what Radarr and Sonarr let a library be organised by:
    /// studio, network, and whether a show has ended. A show that has finished
    /// and is missing episodes is a different problem from one still airing
    /// them, and until now Deluno could not tell the two apart.
    /// </summary>
    string? Certification = null,
    string? Studio = null,
    string? Network = null,
    string? Collection = null,
    string? Director = null,
    string? TrailerUrl = null,
    string? Tagline = null,
    string? Homepage = null,
    string? OriginalLanguage = null,
    /// <summary><c>Released</c> / <c>In Production</c>, or <c>Ended</c> / <c>Returning Series</c>.</summary>
    string? Status = null);

/// <summary>
/// When a movie can actually be obtained. A cinema date is not an availability
/// date — searching on it wastes every cycle until a digital release exists.
/// </summary>
public sealed record MetadataReleaseDates(
    DateOnly? InCinemas,
    DateOnly? Digital,
    DateOnly? Physical)
{
    public static readonly MetadataReleaseDates None = new(null, null, null);

    public bool HasAny => InCinemas is not null || Digital is not null || Physical is not null;
}

/// <summary>One season of a series as the provider describes it.</summary>
public sealed record MetadataSeason(
    int SeasonNumber,
    string? Name,
    int EpisodeCount,
    DateOnly? AirDate,
    IReadOnlyList<MetadataEpisode> Episodes);

/// <summary>
/// One episode as the provider describes it — whether or not a file for it
/// exists. The catalogue is the provider's; what is on disk is an overlay.
/// </summary>
public sealed record MetadataEpisode(
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    DateTimeOffset? AirDateUtc);

public sealed record MetadataCastMember(
    string Name,
    string? Character,
    string? ProfileUrl);

public sealed record MetadataRatingItem(
    string Source,
    string Label,
    double? Score,
    double? MaxScore,
    int? VoteCount,
    string? Url,
    string? Kind);

public sealed record MetadataLookupRequest(
    string? Query,
    string? MediaType,
    int? Year,
    string? ProviderId);

public sealed record MetadataProviderStatus(
    string Provider,
    bool IsConfigured,
    string Mode,
    string Message,
    IReadOnlyList<MetadataSourceStatus> Sources);

public sealed record MetadataSourceStatus(
    string Source,
    string Label,
    string Role,
    bool IsConfigured,
    string Mode,
    string Message);
