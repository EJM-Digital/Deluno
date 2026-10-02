using System.Text.Json.Serialization;
using Deluno.Contracts;

namespace Deluno.Connections.Contracts;

/// <summary>The media servers Deluno can talk to (#493).</summary>
public static class MediaServerKinds
{
    public const string Plex = "plex";
    public const string Jellyfin = "jellyfin";
    public const string Emby = "emby";

    public static readonly IReadOnlyList<string> All = [Plex, Jellyfin, Emby];

    public static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Plex => Plex,
            Jellyfin => Jellyfin,
            Emby => Emby,
            _ => null
        };

    public static string DisplayName(string kind)
        => kind switch
        {
            Plex => "Plex",
            Jellyfin => "Jellyfin",
            Emby => "Emby",
            _ => kind
        };
}

/// <summary>
/// One configured Plex, Jellyfin or Emby server.
///
/// <para>A server is a Connection like an indexer or a download client: the same
/// health vocabulary, a token that never leaves the server
/// (<see cref="HasToken"/> goes instead), and a test the owner can run.</para>
///
/// <para><see cref="ServerName"/> and <see cref="ServerId"/> are what the server
/// said about itself on its last successful test. They are shown beside the
/// address so a person can see which machine they actually connected to — two
/// servers on the same default port are otherwise easy to confuse, and a
/// retention rule removing items from the wrong one would be the worst kind of
/// mistake.</para>
/// </summary>
public sealed record MediaServerConnection(
    string Id,
    string Kind,
    string Name,
    string BaseUrl,
    [property: JsonIgnore] string? Token,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    string? ServerName,
    string? ServerId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>The saved secret was stored on another computer, account or key and cannot be read here (#1094). It is listed as missing; entering it again clears this.</summary>
    public bool SecretUnreadable { get; init; }

    /// <summary>Plain-words reason when <see cref="SecretUnreadable"/> is set, for the screen and Health.</summary>
    public string? SecretProblem { get; init; }

    /// <summary>Last typed failure from a test or a real request.</summary>
    public IntegrationFailure? LastHealthFailure { get; init; }

    /// <summary>Plex: show the Leaving soon collections on home (#521).</summary>
    public bool LeavingSoonOnHome { get; init; }
}

/// <param name="Token">
/// Plex: an <c>X-Plex-Token</c>. Jellyfin and Emby: an API key. Blank keeps the
/// stored one, because the screen cannot show what is saved.
/// </param>
public sealed record SaveMediaServerRequest(
    string? Kind,
    string? Name,
    string? BaseUrl,
    string? Token,
    bool IsEnabled,
    bool? LeavingSoonOnHome = null);

/// <summary>What a test found out, in the words the screen prints.</summary>
public sealed record MediaServerTestResult(
    bool Ok,
    string Status,
    string Message,
    int? LatencyMs,
    string? ServerName = null,
    string? ServerId = null,
    int? LibraryCount = null,
    int? UserCount = null,
    IntegrationFailure? Failure = null,
    // The version the server reported (#1117), "1.43.4.10903" or "10.10.7"; null when it did not say.
    string? Version = null);
