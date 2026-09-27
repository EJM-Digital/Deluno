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
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// Where an approved movie/series request lands (#851), the same
    /// library+quality-profile pair a manual add already takes. Null follows
    /// the ordinary fallback: the first library of that media type.
    /// </summary>
    string? MovieLibraryId = null,
    string? MovieQualityProfileId = null,
    string? SeriesLibraryId = null,
    string? SeriesQualityProfileId = null)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <param name="ApiKey">Blank keeps the stored one, because the screen cannot show what is saved.</param>
public sealed record SaveSeerrRequest(
    string? Name,
    string? BaseUrl,
    string? ApiKey,
    bool IsEnabled,
    string? MovieLibraryId = null,
    string? MovieQualityProfileId = null,
    string? SeriesLibraryId = null,
    string? SeriesQualityProfileId = null);

/// <summary>What a Seerr test found out, in the words the screen prints.</summary>
public sealed record SeerrTestResult(
    bool Ok,
    string Status,
    string Message,
    int? LatencyMs,
    string? ServerName = null,
    string? Version = null);

/// <summary>
/// One approval Deluno turned into an acquisition (#851), or tried to and
/// failed — the log the Seerr settings card shows so the automatic action is
/// visible, not silent. Also how an import (#852) finds its way back to the
/// Seerr server and media id to report availability to.
/// </summary>
public sealed record SeerrAcquisitionItem(
    string Id,
    string ConnectionId,
    string ConnectionName,
    string MediaType,
    string EntityId,
    string Title,
    int SeerrMediaId,
    /// <summary>"added", "search-failed", "available" or "available-failed".</summary>
    string Status,
    string? Message,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
