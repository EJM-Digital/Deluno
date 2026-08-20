namespace Deluno.Connections;

/// <summary>
/// Values persisted by the Connections module. The three legacy values remain
/// loadable so an existing connection is never silently rewritten; Integrations
/// reports them as recognised but not implemented.
/// </summary>
internal static class DownloadClientProtocolCatalog
{
    private static readonly HashSet<string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        "qbittorrent", "sabnzbd", "nzbget", "transmission", "deluge", "utorrent",
        "custom", "usenet", "torrent"
    };

    internal static bool IsAccepted(string? protocol)
        => !string.IsNullOrWhiteSpace(protocol) && Accepted.Contains(protocol.Trim());

    internal static string NormalizeOrThrow(string? protocol)
    {
        if (IsAccepted(protocol)) return protocol!.Trim().ToLowerInvariant();
        throw new ArgumentException($"'{protocol}' is not a supported download client protocol. Supported protocols: {string.Join(", ", Accepted.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}.", nameof(protocol));
    }

    internal static string SupportedProtocols => string.Join(", ", Accepted.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
}
