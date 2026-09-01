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
}
