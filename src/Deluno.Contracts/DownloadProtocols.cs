namespace Deluno.Contracts;

/// <summary>
/// What a download protocol does *after* a download finishes, which is the
/// only thing about it several modules need to agree on (#287, #288).
///
/// Usenet finishes and is done: the file is the user's, and deleting it is a
/// perfectly ordinary thing to do. A torrent goes on being shared, and the
/// client keeps believing it owns the file — so deleting it from underneath
/// breaks the share and, on a private site, costs the user their account.
///
/// That single distinction decides who is allowed to remove a completed
/// download, so it lives here rather than being spelled out separately in the
/// import pipeline and the sharing rule and drifting apart.
/// </summary>
public static class DownloadProtocols
{
    private static readonly HashSet<string> WithoutSharingPhase = new(StringComparer.OrdinalIgnoreCase)
    {
        "sabnzbd",
        "nzbget",
        "usenet"
    };

    /// <summary>
    /// True when the client keeps sharing a completed download — every torrent
    /// protocol, and anything Deluno does not recognise.
    ///
    /// The unknown case answers true deliberately: assuming a strange client
    /// shares means Deluno asks it to remove its own file instead of deleting
    /// one behind its back, and being over-careful there costs disk while being
    /// under-careful costs someone their tracker account.
    /// </summary>
    public static bool HasSharingPhase(string? protocol)
        => string.IsNullOrWhiteSpace(protocol) || !WithoutSharingPhase.Contains(protocol.Trim());

    public const string Usenet = "usenet";
    public const string Torrent = "torrent";

    /// <summary>
    /// What a download client can be handed: <c>usenet</c> (an NZB) or
    /// <c>torrent</c>. The same split as <see cref="HasSharingPhase"/>, asked
    /// from the other end.
    ///
    /// <para>Nothing used to ask. A library's client was chosen before its
    /// search ran, by priority alone, so a library routed to both qBittorrent
    /// and SABnzbd sent a torrent to SABnzbd. SABnzbd accepted the address, read
    /// the .torrent as an NZB every thirty seconds for five minutes and gave up,
    /// and Deluno recorded the grab as succeeded.</para>
    /// </summary>
    public static string ClientKind(string? clientProtocol)
        => HasSharingPhase(clientProtocol) ? Torrent : Usenet;

    /// <summary>What a search source offers: Newznab is Usenet, everything else is torrents.</summary>
    public static string SourceKind(string? indexerProtocol)
        => string.Equals(indexerProtocol?.Trim(), "newznab", StringComparison.OrdinalIgnoreCase) ? Usenet : Torrent;
}
