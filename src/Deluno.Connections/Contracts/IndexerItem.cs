using System.Text.Json.Serialization;

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
}
