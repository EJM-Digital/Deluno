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
    string? ExternalUrl);

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
