using System.Text.Json.Serialization;

namespace Deluno.Connections.Contracts;

/// <summary>
/// One configured subtitle source, as the API hands it back.
///
/// <para>The health half of this record is the indexer's health half, field for
/// field, and deliberately so: DESIGN-002 rule 4 says providers are Connections,
/// which means "is this source working" has one answer in Deluno rather than one
/// per kind of source.</para>
///
/// <para><see cref="Secret"/> and <see cref="ApiKey"/> never leave the server.
/// <see cref="HasSecret"/> and <see cref="HasApiKey"/> go instead, because a
/// settings screen has to be able to say "a password is saved" without being
/// told what it is — the same trick <c>IndexerItem</c> plays with its API key.</para>
/// </summary>
public sealed record SubtitleProviderConnection(
    string Id,
    string ProviderKey,
    string Name,
    string? Username,
    [property: JsonIgnore] string? Secret,
    [property: JsonIgnore] string? ApiKey,
    int Priority,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    int ConsecutiveFailures,
    DateTimeOffset? RateLimitedUntilUtc,
    string? DisabledReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Whether this source is one Deluno will actually ask right now.
    ///
    /// <para>Enabled and not inside a rate-limit window. Both, because a source
    /// that is rate limited is working — asking it again before it said to would
    /// spend the next answer as well.</para>
    /// </summary>
    public bool IsAskable(DateTimeOffset now)
        => IsEnabled && (RateLimitedUntilUtc is null || RateLimitedUntilUtc <= now);
}

/// <summary>
/// What Deluno ships, whether it is configured, and what it would need.
///
/// <para>One row per provider on the settings screen, present whether or not
/// anybody has set it up — because "which sources exist" is a fact about the
/// build, and a screen that only lists what you have already added cannot tell
/// you what you are missing.</para>
/// </summary>
public sealed record SubtitleProviderOption(
    string Key,
    string DisplayName,
    string Description,
    /// <summary><c>both</c>, <c>movies</c> or <c>tv</c>.</summary>
    string Scope,
    bool NeedsUsername,
    bool NeedsPassword,
    bool NeedsApiKey,
    /// <summary>
    /// Whether the credentials above are optional. Podnapisi answers without an
    /// account and answers better with one, and saying so is the difference
    /// between "needs an account" and "an account gets you more".
    /// </summary>
    bool CredentialsOptional,
    SubtitleProviderConnection? Configured);

public sealed record SaveSubtitleProviderRequest(
    string? ProviderKey,
    string? Username,
    string? Secret,
    string? ApiKey,
    int? Priority,
    bool IsEnabled);

/// <summary>What a test found out, in the words the screen prints.</summary>
public sealed record SubtitleProviderTestResult(
    bool Ok,
    string Status,
    string Message,
    int? LatencyMs,
    int? ResultCount);
