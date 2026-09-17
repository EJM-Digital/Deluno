using System.Text.Json.Serialization;

namespace Deluno.Connections.Contracts;

/// <summary>
/// A Seerr server - Jellyseerr or Overseerr - where people request titles (#522).
///
/// <para>A Connection like a media server: the key never leaves Deluno
/// (<see cref="HasApiKey"/> goes instead), and a test reports the name and
/// version the server gave. Retention reads who requested what, and clears a
/// title's record when a rule removes the whole title.</para>
/// </summary>
/// <param name="Version">What the server said its version was on its last successful test.</param>
public sealed record SeerrConnection(
    string Id,
    string Name,
    string BaseUrl,
    [property: JsonIgnore] string? ApiKey,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    string? ServerName,
    string? Version,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <param name="ApiKey">Blank keeps the stored one, because the screen cannot show what is saved.</param>
public sealed record SaveSeerrRequest(string? Name, string? BaseUrl, string? ApiKey, bool IsEnabled);

/// <summary>What a Seerr test found out, in the words the screen prints.</summary>
public sealed record SeerrTestResult(
    bool Ok,
    string Status,
    string Message,
    int? LatencyMs,
    string? ServerName = null,
    string? Version = null);
