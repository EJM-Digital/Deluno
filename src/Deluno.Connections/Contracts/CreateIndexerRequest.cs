namespace Deluno.Connections.Contracts;

public sealed record CreateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    string? ApiKey,
    int? Priority,
    string? Categories,
    string? Tags,
    string? MediaScope,
    bool IsEnabled,
    int? RequestIntervalSeconds = null,
    // Non-default Newznab/Torznab API path (#740). Null/blank uses /api.
    string? ApiPath = null,
    // Extra Newznab/Torznab query parameters some indexers require (#741).
    string? AdditionalParameters = null,
    // This indexer's own anime category numbers (#742). Null uses Categories.
    string? AnimeCategories = null,
    // Null means "inherit the global sharing rule" (#288).
    string? SharingMode = null,
    int? SharingForHours = null,
    double? SharingUntilRatio = null,
    string? SharingStuckAction = null,
    int? SharingStuckAfterDays = null,
    // A longer seed time used for season packs instead of SharingForHours (#739).
    int? SeasonPackSeedForHours = null,
    int? MinimumAgeMinutes = null,
    int? RetentionDays = null,
    int? MaximumSizeMb = null,
    // The seeder floor a torrent from this indexer must clear to be grabbed
    // (#738). Null means unrestricted.
    int? MinimumSeeders = null,
    string? PreferIndexerFlags = null,
    // Comma-separated, case-insensitive keyword lists that reject a release
    // before grab (#744), separate from PreferIndexerFlags' ranking nudge.
    string? RequiredWords = null,
    string? IgnoredWords = null,
    // The languages this indexer's own MULTI tag means (#746). Null uses
    // Deluno's fixed "Multilingual" default.
    string? MultiLanguages = null,
    // The recorded VIP/subscription expiry date for a private-tracker
    // indexer (#747). Null means nothing is tracked.
    DateTimeOffset? VipExpiresUtc = null,
    // A hard ceiling on searches/grabs per day (default) or hour, in
    // addition to RequestIntervalSeconds' pacing (#750).
    int? QueryLimit = null,
    int? GrabLimit = null,
    string? LimitsUnit = null,
    // For a torrent indexer, prefer a magnet URL over a .torrent file link
    // when a grab has both (#752). Ignored for usenet. Off by default.
    bool PreferMagnetUrl = false,
    // Restricts TV searches to season packs only, episodes only, or both
    // (default) (#743). Null or "both" means no restriction.
    string? SeasonPackSearchMode = null,
    int? AvailabilityDelayDays = null,
    bool RssEnabled = true,
    bool AutomaticSearchEnabled = true,
    bool InteractiveSearchEnabled = true,
    string? DefinitionId = null,
    IReadOnlyDictionary<string, string>? DefinitionSettings = null,
    IReadOnlyDictionary<string, string>? DefinitionSecrets = null,
    // The configured proxy (#748) this indexer's requests go through. Null
    // means "no proxy assigned" -- direct connection.
    string? ProxyId = null,
    // The result of a test the person ran in the drawer before adding this
    // indexer (#1134), kept with it so the list shows it straight away.
    IndexerTestedHealth? TestedHealth = null,
    // #1135: switch the new indexer on for the libraries of its media types. The drawer opt-out sends false.
    bool RouteToLibraries = true);

/// <summary>A test result carried in with a new indexer (#1134).</summary>
public sealed record IndexerTestedHealth(string? HealthStatus, string? Message, string? FailureCategory, int? LatencyMs);
