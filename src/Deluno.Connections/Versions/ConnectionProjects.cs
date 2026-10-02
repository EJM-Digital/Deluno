using Deluno.Connections.Contracts;

namespace Deluno.Connections.Versions;

/// <summary>
/// The projects whose releases Deluno compares a connection's version with (#1117). A connection whose
/// project is null says its own version and nothing more.
/// </summary>
public static class ConnectionProjects
{
    public const string Qbittorrent = "qbittorrent";
    public const string Transmission = "transmission";
    public const string Deluge = "deluge";
    public const string Sabnzbd = "sabnzbd";
    public const string Nzbget = "nzbget";
    public const string Plex = "plex";
    public const string Jellyfin = "jellyfin";
    public const string Emby = "emby";
    public const string Seerr = "seerr";
    public const string Weir = "weir";
    public const string Prowlarr = "prowlarr";
    public const string Jackett = "jackett";

    /// <summary>The release feed for a download client's protocol; null for clients with no feed (uTorrent, rTorrent).</summary>
    public static string? ForDownloadClient(string? protocol)
        => protocol?.Trim().ToLowerInvariant() switch
        {
            "qbittorrent" => Qbittorrent,
            "transmission" => Transmission,
            "deluge" => Deluge,
            "sabnzbd" => Sabnzbd,
            "nzbget" => Nzbget,
            _ => null
        };

    public static string? ForMediaServer(string? kind)
        => MediaServerKinds.Normalize(kind);
}
