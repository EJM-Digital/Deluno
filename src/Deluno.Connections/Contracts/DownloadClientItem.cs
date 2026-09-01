namespace Deluno.Connections.Contracts;

using System.Text.Json.Serialization;
using Deluno.Contracts;

public sealed record DownloadClientItem(
    string Id,
    string Name,
    string Protocol,
    string? Host,
    int? Port,
    string? Username,
    [property: JsonIgnore] string? Secret,
    string? EndpointUrl,
    string? MoviesCategory,
    string? TvCategory,
    string? CategoryTemplate,
    int Priority,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    string? LastHealthFailureCategory,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Last typed health failure, when the client did not pass its most recent test.</summary>
    public IntegrationFailure? LastHealthFailure { get; init; }
}
