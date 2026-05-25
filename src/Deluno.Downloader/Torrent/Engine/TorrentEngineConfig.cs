namespace Deluno.Downloader.Torrent.Engine;

/// <summary>
/// User-tunable knobs for the torrent engine. Persisted as a single
/// JSON file under <c>&lt;dataRoot&gt;/downloader/torrent-config.json</c>.
///
/// <para><b>Why not SQLite</b>: this is one record, low write-rate,
/// loaded once at startup. A JSON file beats adding a settings table
/// + migration + repository for one row.</para>
///
/// <para><b>Defaults</b>: chosen to mirror qBittorrent's defaults so
/// users migrating from a qBit setup get familiar behavior out of the
/// box. UPnP/LSD off by default because they leak on private trackers
/// — users can opt in for public-only setups via the Settings UI.</para>
/// </summary>
/// <param name="ListenPort">
/// TCP/UDP port for incoming peer connections. 51413 matches
/// qBittorrent's default — common firewall rules already know about it.
/// 0 = ephemeral (test/CI mode).
/// </param>
/// <param name="AllowUpnp">
/// Auto-forward the listen port via the router's UPnP IGD.
/// Default false: convenient for public-tracker users on home LANs,
/// risky on private trackers (leaks the listen port to NAT-traversal
/// services). User opts in.
/// </param>
/// <param name="AllowLsd">
/// Local Service Discovery — multicast peer announcement on LAN.
/// Same risk profile as UPnP.
/// </param>
/// <param name="MaxGlobalConnections">
/// Cap across ALL torrents. MonoTorrent's default is 200.
/// </param>
/// <param name="MaxUploadBytesPerSecond">
/// Global upload throttle in bytes/second. 0 = unlimited.
/// </param>
/// <param name="MaxDownloadBytesPerSecond">
/// Global download throttle in bytes/second. 0 = unlimited.
/// </param>
/// <param name="DefaultRatioTarget">
/// Default seed-until-ratio for new torrents. 1.0 = seed back what
/// was downloaded (private-tracker friendly default). null = no ratio
/// target (seed forever or until time target hits).
/// </param>
/// <param name="DefaultSeedTimeTargetMinutes">
/// Default seed-time target for new torrents. Stops seeding after
/// this many minutes past completion regardless of ratio. null = no
/// time target.
/// </param>
/// <param name="MagnetMetadataTimeoutSeconds">
/// Cap on BEP-9 metadata fetch when adding a magnet link. Default
/// 300 (5 minutes) — magnets with active trackers resolve in seconds;
/// the long cap is for sparse swarms or DHT-only public torrents.
/// </param>
public sealed record TorrentEngineConfig(
    int ListenPort = 51413,
    bool AllowUpnp = false,
    bool AllowLsd = false,
    int MaxGlobalConnections = 200,
    int MaxUploadBytesPerSecond = 0,
    int MaxDownloadBytesPerSecond = 0,
    double? DefaultRatioTarget = 1.0,
    int? DefaultSeedTimeTargetMinutes = null,
    int MagnetMetadataTimeoutSeconds = 300)
{
    /// <summary>The hardcoded default — used when no config file exists yet.</summary>
    public static TorrentEngineConfig Defaults => new();

    /// <summary>
    /// Map this config into the engine's runtime-options record.
    /// </summary>
    public MonoTorrentEngineOptions ToEngineOptions(string cacheDir, string defaultDownloadDir)
        => new(
            CacheDir: cacheDir,
            DefaultDownloadDir: defaultDownloadDir,
            ListenPort: ListenPort,
            AllowUpnp: AllowUpnp,
            AllowLsd: AllowLsd,
            MaxGlobalConnections: MaxGlobalConnections,
            MaxUploadBytesPerSecond: MaxUploadBytesPerSecond,
            MaxDownloadBytesPerSecond: MaxDownloadBytesPerSecond)
        {
            MagnetMetadataTimeout = TimeSpan.FromSeconds(MagnetMetadataTimeoutSeconds),
        };
}

/// <summary>
/// Persistence boundary for <see cref="TorrentEngineConfig"/>.
/// </summary>
public interface ITorrentEngineConfigStore
{
    Task<TorrentEngineConfig> LoadAsync(CancellationToken ct);
    Task SaveAsync(TorrentEngineConfig config, CancellationToken ct);
}
