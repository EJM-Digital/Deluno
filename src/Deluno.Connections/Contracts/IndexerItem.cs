using System.Text.Json.Serialization;
using Deluno.Contracts;

namespace Deluno.Connections.Contracts;

public sealed record IndexerItem(
    string Id,
    string Name,
    string Protocol,
    string Privacy,
    string BaseUrl,
    [property: JsonIgnore]
    string? ApiKey,
    int Priority,
    string Categories,
    string Tags,
    string MediaScope,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    string? LastHealthFailureCategory,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    int ConsecutiveFailures,
    DateTimeOffset? RateLimitedUntilUtc,
    string? DisabledReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>
    /// Optional user-configured minimum interval between requests to this
    /// indexer, in seconds. Null uses Deluno's safe two-second default.
    /// </summary>
    public int? RequestIntervalSeconds { get; init; }

    /// <summary>
    /// A non-default Newznab/Torznab API path (#740), such as
    /// <c>/api/v2</c>. Null or blank uses the protocol's standard
    /// <c>/api</c> path.
    /// </summary>
    public string? ApiPath { get; init; }

    /// <summary>
    /// Extra Newznab/Torznab query parameters some indexers require (#741),
    /// stored as a raw query string fragment (e.g. <c>token=abc&amp;extra=1</c>)
    /// and appended to every search request sent to this indexer.
    /// </summary>
    public string? AdditionalParameters { get; init; }

    /// <summary>
    /// This indexer's own anime category numbers (#742), in the same
    /// comma-separated format as <see cref="Categories"/>. Anime is often
    /// catalogued under different category numbers than normal TV on the
    /// same indexer; null uses <see cref="Categories"/> for every search.
    /// </summary>
    public string? AnimeCategories { get; init; }

    /// <summary>
    /// This source's own sharing rule (#288). Every field is null by default,
    /// meaning "inherit the global setting", so a source only has to state what
    /// makes it different — a private tracker that wants a ratio, say, on an
    /// install where everything else is happy with the global three days.
    /// </summary>
    public string? SharingMode { get; init; }

    public int? SharingForHours { get; init; }

    public double? SharingUntilRatio { get; init; }

    public string? SharingStuckAction { get; init; }

    public int? SharingStuckAfterDays { get; init; }

    /// <summary>
    /// Acquisition controls are nullable where an empty value means
    /// "unlimited" or "use the release profile". The three search-kind flags
    /// are true by default so existing indexers keep their current behaviour.
    /// </summary>
    public int? MinimumAgeMinutes { get; init; }

    public int? RetentionDays { get; init; }

    public int? MaximumSizeMb { get; init; }

    /// <summary>
    /// The seeder floor a torrent from this indexer must clear to be grabbed
    /// (#738). Null means unrestricted; ignored for usenet/RSS indexers,
    /// which have no seeder count to check.
    /// </summary>
    public int? MinimumSeeders { get; init; }

    public string? PreferIndexerFlags { get; init; }

    /// <summary>
    /// Comma-separated words a release's title must contain (case-insensitive)
    /// to be grabbed from this indexer; null or empty means no requirement
    /// (#744). Separate from <see cref="PreferIndexerFlags"/>, which only
    /// nudges ranking and never rejects a release.
    /// </summary>
    public string? RequiredWords { get; init; }

    /// <summary>
    /// Comma-separated words that reject a release from this indexer
    /// (case-insensitive) before it can be grabbed (#744).
    /// </summary>
    public string? IgnoredWords { get; init; }

    /// <summary>
    /// The languages this indexer's own MULTI tag means (#746), e.g.
    /// "Original, French". Null uses Deluno's fixed "Multilingual" default.
    /// </summary>
    public string? MultiLanguages { get; init; }

    /// <summary>
    /// The recorded VIP/subscription expiry date for a private-tracker
    /// indexer (#747). Null means nothing is tracked.
    /// </summary>
    public DateTimeOffset? VipExpiresUtc { get; init; }

    /// <summary>
    /// Which <see cref="VipExpiresUtc"/> value the user last acknowledged a
    /// warning for (#747). The warning clears once this matches the current
    /// value, whether from an explicit acknowledgement or from the date
    /// itself being updated.
    /// </summary>
    public DateTimeOffset? VipWarningAcknowledgedExpiryUtc { get; init; }

    /// <summary>
    /// A hard ceiling on searches sent to this indexer per <see cref="LimitsUnit"/>
    /// (#750), in addition to <see cref="RequestIntervalSeconds"/>'s pacing.
    /// Null means unrestricted.
    /// </summary>
    public int? QueryLimit { get; init; }

    /// <summary>A hard ceiling on grabs sent to this indexer per <see cref="LimitsUnit"/> (#750). Null means unrestricted.</summary>
    public int? GrabLimit { get; init; }

    /// <summary>"day" (default) or "hour" — the window <see cref="QueryLimit"/> and <see cref="GrabLimit"/> count against (#750).</summary>
    public string? LimitsUnit { get; init; }

    public int? AvailabilityDelayDays { get; init; }

    public bool RssEnabled { get; init; } = true;

    public bool AutomaticSearchEnabled { get; init; } = true;

    public bool InteractiveSearchEnabled { get; init; } = true;

    /// <summary>Last typed health failure, when the source did not pass its most recent test.</summary>
    public IntegrationFailure? LastHealthFailure { get; init; }

    /// <summary>The built-in tracker a native tracker indexer runs (#606); null for Newznab, Torznab and RSS.</summary>
    public string? DefinitionId { get; init; }

    /// <summary>A tracker's settings that are not secret (sort order, filters), by setting name.</summary>
    public IReadOnlyDictionary<string, string>? DefinitionSettings { get; init; }

    /// <summary>A tracker's secrets (password, cookie, key). Never serialized; <see cref="DefinitionSecretsSet"/> says which are there.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? DefinitionSecrets { get; init; }

    /// <summary>Which secrets are saved, so the screen can say "saved" without seeing them.</summary>
    public IReadOnlyList<string> DefinitionSecretsSet => DefinitionSecrets?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [];
}
