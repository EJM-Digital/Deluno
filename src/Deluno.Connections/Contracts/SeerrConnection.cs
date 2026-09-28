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
    string? SeriesQualityProfileId = null,
    /// <summary>
    /// The value Overseerr/Jellyseerr's webhook agent must send back as its
    /// Authorization header (#851). Never serialized to the API directly —
    /// only the dedicated reveal/regenerate endpoints return the plaintext,
    /// the same way the API key is never handed back once saved.
    /// </summary>
    [property: JsonIgnore] string? WebhookSecret = null,
    /// <summary>
    /// Where a 4K approval lands (#985): Deluno is built for multiple
    /// libraries, so a request Seerr marked 4K gets its own library and
    /// quality profile rather than sharing the normal one. Null means no 4K
    /// library is mapped yet.
    /// </summary>
    string? Movie4kLibraryId = null,
    string? Movie4kQualityProfileId = null,
    string? Series4kLibraryId = null,
    string? Series4kQualityProfileId = null,
    /// <summary>
    /// What a 4K request does when no 4K library is mapped (#985). False (the
    /// default) refuses the request and writes why to Activity in plain
    /// words; true falls back to the normal library and profile instead.
    /// </summary>
    bool Fallback4kToNormalLibrary = false)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasWebhookSecret => !string.IsNullOrWhiteSpace(WebhookSecret);
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
    string? SeriesQualityProfileId = null,
    string? Movie4kLibraryId = null,
    string? Movie4kQualityProfileId = null,
    string? Series4kLibraryId = null,
    string? Series4kQualityProfileId = null,
    bool Fallback4kToNormalLibrary = false);

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
    DateTimeOffset UpdatedUtc,
    /// <summary>Who asked for it in Seerr (#985), the way Seerr showed them; null when the webhook sent none.</summary>
    string? RequestedBy = null);
