namespace Deluno.Connections;

/// <summary>
/// Which download client protocols this module will store, and which of those a
/// user may actually choose.
///
/// The two sets differ on purpose. <see cref="Loadable"/> still contains three
/// legacy values so an existing connection is never silently rewritten when it
/// is read back. <see cref="Dispatchable"/> is the set Integrations can really
/// send a release to, and it is the only set a create or update may name: a
/// client saved as "torrent" is reachable, tests healthy, and cannot receive
/// anything, and that only surfaced once a search had run and a release had been
/// chosen (#292). Reject it at the moment it is written instead.
/// </summary>
internal static class DownloadClientProtocolCatalog
{
    private static readonly HashSet<string> Dispatchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "qbittorrent", "sabnzbd", "nzbget", "transmission", "deluge", "utorrent"
    };

    private static readonly HashSet<string> Loadable = new(Dispatchable, StringComparer.OrdinalIgnoreCase)
    {
        "custom", "usenet", "torrent"
    };

    /// <summary>A value this module will read back from storage.</summary>
    internal static bool IsAccepted(string? protocol)
        => !string.IsNullOrWhiteSpace(protocol) && Loadable.Contains(protocol.Trim());

    /// <summary>A value a release can actually be sent to.</summary>
    internal static bool IsDispatchable(string? protocol)
        => !string.IsNullOrWhiteSpace(protocol) && Dispatchable.Contains(protocol.Trim());

    internal static string NormalizeOrThrow(string? protocol)
    {
        if (IsAccepted(protocol)) return protocol!.Trim().ToLowerInvariant();
        throw new ArgumentException($"'{protocol}' is not a supported download client protocol. Supported protocols: {SupportedProtocols}.", nameof(protocol));
    }

    internal static string SupportedProtocols => string.Join(", ", Dispatchable.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
}
