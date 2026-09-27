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
    bool UseSsl,
    string? UrlBase,
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
    DateTimeOffset UpdatedUtc,
    bool AddPaused = false,
    /// <summary>Comma-separated tag names (#808). Empty means the client is used for any title; otherwise it is used only for a title sharing one of these tags.</summary>
    string Tags = "",
    /// <summary>Remove a download from this client once Deluno has imported it successfully (#806). Off by default (#936); replaces the old platform-wide RemoveCompletedDownloads flag per client.</summary>
    bool RemoveCompleted = false,
    /// <summary>Remove a download from this client once Deluno records it as failed (#806). Distinct from ImportFailurePolicy, which decides whether the release is blocklisted and searched again; this only decides whether the client keeps the failed entry. Off by default (#936).</summary>
    bool RemoveFailed = false)
{
    /// <summary>Last typed health failure, when the client did not pass its most recent test.</summary>
    public IntegrationFailure? LastHealthFailure { get; init; }
}
