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
    // No Prowlarr or Jackett: Deluno replaces them, so it never reports their versions (James, 3 Oct 2026).

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

    /// <summary>The name a person knows the project by ("qBittorrent", "SABnzbd").</summary>
    public static string DisplayName(string project)
        => project switch
        {
            Qbittorrent => "qBittorrent",
            Transmission => "Transmission",
            Deluge => "Deluge",
            Sabnzbd => "SABnzbd",
            Nzbget => "NZBGet",
            Plex => "Plex",
            Jellyfin => "Jellyfin",
            Emby => "Emby",
            Seerr => "Seerr",
            Weir => "Weir",
            _ => project
        };

    public static string? ForMediaServer(string? kind)
        => MediaServerKinds.Normalize(kind);
}
