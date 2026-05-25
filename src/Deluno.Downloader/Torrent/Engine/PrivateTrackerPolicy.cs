namespace Deluno.Downloader.Torrent.Engine;

/// <summary>
/// Encodes the 13-point private-tracker compliance list from the
/// architecture doc. All enforced at announce/connection time, not at
/// config time — so a misconfigured public-torrent global setting can
/// never leak a private torrent's infohash. Bans are forever and they
/// happen instantly; this is the single most important correctness
/// surface in the torrent module.
///
/// This class is a pure-data policy object; the engine applies it by
/// adjusting MonoTorrent's per-torrent settings when a torrent's
/// metadata reveals <c>info.private = 1</c>, or when a magnet is added
/// for a private-suspect category (see <c>MagnetIngestor</c> for the
/// leak-window mitigation).
/// </summary>
public static class PrivateTrackerPolicy
{
    /// <summary>
    /// Returns the per-torrent settings overrides that MUST be applied
    /// when <see cref="TorrentJobHandle.IsPrivate"/> is true. Any
    /// setting NOT in this list inherits the global default.
    /// </summary>
    public static PrivateTorrentOverrides Required => new(
        // 1. DHT off — leaking the infohash to the Mainline DHT bans
        //    accounts on every major private tracker.
        DhtEnabled: false,
        // 2. PEX off — peer-exchange between connected peers can advertise
        //    the infohash. Same ban consequence as DHT.
        PexEnabled: false,
        // 3. LSD off — Local Service Discovery multicasts the infohash
        //    on the LAN. Less severe but still bannable.
        LsdEnabled: false,
        // 4. event=stopped on pause / shutdown — failing to send this
        //    makes the tracker think you've abandoned the torrent
        //    (HnR flag); enforce at the announce layer.
        SendStoppedOnPause: true,
        // 5. Passkey preserved on tracker URL rewrite — the engine
        //    must follow 301/307 redirects without dropping
        //    `passkey=...` from the path / query.
        PreservePasskeyAcrossRedirects: true,
        // 6. Single-IP enforcement — bind outgoing connections to one
        //    interface; users get a warning if multiple interfaces are
        //    configured.
        BindToSingleOutboundIp: true,
        // 7. Honor `min interval` strictly — even on force re-announce.
        StrictMinInterval: true,
        // 8. HTTPS never downgrades to HTTP — MitM would harvest the passkey.
        ProhibitHttpsDowngrade: true,
        // 9. mandatory key= parameter (random per-torrent, stable across re-announce).
        SendStableKey: true,
        // 10. compact=1 + no_peer_id=1 — saves bandwidth + matches qBit/uTorrent.
        SendCompactAndNoPeerId: true,
        // 11. encryption preferred (mode-aware; some trackers demand it).
        PreferEncryptedConnections: true);
}

/// <summary>
/// Bag of per-torrent settings that the engine pushes into
/// MonoTorrent's TorrentSettingsBuilder for a single torrent (vs the
/// global engine defaults).
/// </summary>
public sealed record PrivateTorrentOverrides(
    bool DhtEnabled,
    bool PexEnabled,
    bool LsdEnabled,
    bool SendStoppedOnPause,
    bool PreservePasskeyAcrossRedirects,
    bool BindToSingleOutboundIp,
    bool StrictMinInterval,
    bool ProhibitHttpsDowngrade,
    bool SendStableKey,
    bool SendCompactAndNoPeerId,
    bool PreferEncryptedConnections);
