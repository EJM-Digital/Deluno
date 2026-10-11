namespace Deluno.Connections.Contracts;

/// <summary>
/// Patch-style request — null values mean "leave unchanged".
/// Sent by PUT /api/indexers/{id}
/// </summary>
public sealed record UpdateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    string? ApiKey,
    int? Priority,
    string? Categories,
    string? Tags,
    string? MediaScope,
    bool? IsEnabled,
    int? RequestIntervalSeconds = null,
    bool? ClearRequestInterval = null,
    // Non-default Newznab/Torznab API path (#740). Null means "leave as is";
    // ClearApiPath drops back to the protocol's standard /api path.
    string? ApiPath = null,
    bool? ClearApiPath = null,
    // Extra Newznab/Torznab query parameters some indexers require (#741).
    string? AdditionalParameters = null,
    bool? ClearAdditionalParameters = null,
    // This indexer's own anime category numbers (#742). Null uses Categories.
    string? AnimeCategories = null,
    bool? ClearAnimeCategories = null,
    // Null means "leave as is". ClearSharingPolicy drops the override entirely
    // so the source goes back to inheriting the global rule (#288).
    string? SharingMode = null,
    int? SharingForHours = null,
    double? SharingUntilRatio = null,
    string? SharingStuckAction = null,
    int? SharingStuckAfterDays = null,
    bool? ClearSharingPolicy = null,
    // A longer seed time used for season packs instead of SharingForHours (#739).
    int? SeasonPackSeedForHours = null,
    bool? ClearSeasonPackSeedForHours = null,
    int? MinimumAgeMinutes = null,
    bool? ClearMinimumAge = null,
    int? RetentionDays = null,
    bool? ClearRetention = null,
    int? MaximumSizeMb = null,
    bool? ClearMaximumSize = null,
    int? MinimumSeeders = null,
    bool? ClearMinimumSeeders = null,
    string? PreferIndexerFlags = null,
    bool? ClearPreferIndexerFlags = null,
    string? RequiredWords = null,
    bool? ClearRequiredWords = null,
    string? IgnoredWords = null,
    bool? ClearIgnoredWords = null,
    string? MultiLanguages = null,
    bool? ClearMultiLanguages = null,
    DateTimeOffset? VipExpiresUtc = null,
    bool? ClearVipExpiry = null,
    // Acknowledges the currently-recorded VipExpiresUtc value (existing,
    // or this same request's own new value), silencing its warning until
    // the date changes again (#747).
    bool? AcknowledgeVipExpiry = null,
    int? QueryLimit = null,
    bool? ClearQueryLimit = null,
    int? GrabLimit = null,
    bool? ClearGrabLimit = null,
    string? LimitsUnit = null,
    bool? PreferMagnetUrl = null,
    string? SeasonPackSearchMode = null,
    int? AvailabilityDelayDays = null,
    bool? ClearAvailabilityDelay = null,
    bool? RssEnabled = null,
    bool? AutomaticSearchEnabled = null,
    bool? InteractiveSearchEnabled = null,
    // A tracker's settings replace the saved ones when given (#606). Secrets
    // merge instead: a name given sets that secret, an empty value removes
    // it, and a secret not named is left as it was, because the screen never
    // has it to send back.
    IReadOnlyDictionary<string, string>? DefinitionSettings = null,
    IReadOnlyDictionary<string, string>? DefinitionSecrets = null,
    // The configured proxy (#748) this indexer's requests go through. Null
    // means "leave as is"; ClearProxy drops back to a direct connection.
    string? ProxyId = null,
    bool? ClearProxy = null);
