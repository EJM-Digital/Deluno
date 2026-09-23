namespace Deluno.Connections.Contracts;

/// <summary>
/// The ways Deluno talks to an indexer, and the one place a stored or requested
/// protocol is read (#606).
///
/// <para>There were two copies of this and they had drifted: the repository
/// knew "usenet" and "torrent" as aliases and the endpoints did not. Anything
/// unknown still falls back to Torznab, the most common torrent protocol.</para>
/// </summary>
public static class IndexerProtocols
{
    public const string Newznab = "newznab";
    public const string Torznab = "torznab";
    public const string Rss = "rss";

    /// <summary>A tracker Deluno searches itself, from a definition, rather than through a protocol the site does not speak.</summary>
    public const string Tracker = "tracker";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Newznab or "usenet" => Newznab,
            Rss => Rss,
            Tracker => Tracker,
            _ => Torznab
        };

    public static bool IsTracker(string? protocol) => string.Equals(protocol, Tracker, StringComparison.OrdinalIgnoreCase);
}
