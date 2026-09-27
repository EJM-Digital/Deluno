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

    public string? PreferIndexerFlags { get; init; }

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
