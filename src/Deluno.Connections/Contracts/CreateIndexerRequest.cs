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
    int? AvailabilityDelayDays = null,
    bool RssEnabled = true,
    bool AutomaticSearchEnabled = true,
    bool InteractiveSearchEnabled = true,
    string? DefinitionId = null,
    IReadOnlyDictionary<string, string>? DefinitionSettings = null,
    IReadOnlyDictionary<string, string>? DefinitionSecrets = null);
