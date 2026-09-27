namespace Deluno.Contracts;

/// <summary>
/// Whether Deluno has a working Trakt OAuth connection (#846) — a real
/// sign-in against the Trakt API rather than scraping a list's public RSS
/// feed, which is what unlocks private lists and Trakt's own built-in
/// Popular/Trending/Anticipated/Top Watched/Recommended lists.
/// </summary>
/// <param name="ClientConfigured">A Trakt app Client ID/Secret has been entered. Required before Connect can run.</param>
/// <param name="Connected">A token pair is stored and has not been explicitly disconnected.</param>
/// <param name="ExpiresUtc">When the current access token expires. Refreshed automatically before every use while the refresh token still works.</param>
/// <param name="NeedsReconnect">The refresh token itself stopped working, so nothing will refresh silently any more — surfaced on the Lists screen with a reconnect action rather than failing quietly.</param>
public sealed record TraktConnectionStatus(
    bool ClientConfigured,
    bool Connected,
    DateTimeOffset? ExpiresUtc,
    bool NeedsReconnect);
