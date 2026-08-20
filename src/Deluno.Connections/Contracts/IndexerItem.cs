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
}
