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
    /// Where an approved request lands when no destination rule matches it
    /// (#1033) - the fallback library and quality profile per media type,
    /// the last resort before Deluno refuses rather than guessing. Before
    /// #1033 this was the only mapping a connection had, and #985 added a
    /// second, 4K-only pair beside it; both ideas are now one rule-based
    /// resolver (see <c>ILibraryRoutingResolver</c>), so ordinary rules —
    /// including a "requested in 4K" one — are tried first and this is what
    /// is left over when none of them match.
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
    /// <summary>Comma-separated tag names given to every title this connection adds (#1046). Empty gives none.</summary>
    string Tags = "",
    /// <summary>Extra tag names given only when the request was for 4K (#1046), on top of <see cref="Tags"/>.</summary>
    string Tags4k = "")
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The saved secret was stored on another computer, account or key and cannot be read here (#1094). It is listed as missing; entering it again clears this.</summary>
    public bool SecretUnreadable { get; init; }

    /// <summary>Plain-words reason when <see cref="SecretUnreadable"/> is set, for the screen and Health.</summary>
    public string? SecretProblem { get; init; }

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
    /// <summary>Tags given to every title this connection adds (#1046). Null keeps what is stored.</summary>
    string? Tags = null,
    /// <summary>Extra tags for a 4K request (#1046). Null keeps what is stored.</summary>
    string? Tags4k = null);

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
    string? RequestedBy = null,
    /// <summary>
    /// The destination rule that placed this request (#1033), in plain words
    /// on Activity and the Seerr card's recent acquisitions. Null when the
    /// connection's fallback library was used instead — no rule matched — or
    /// on a row recorded before #1033.
    /// </summary>
    string? RuleId = null,
    string? RuleName = null);
