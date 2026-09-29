namespace Deluno.Connections.Contracts;

/// <summary>
/// qBittorrent's content layout choices (#999). Radarr and Sonarr offer the
/// same choices (their QBittorrentContentLayout enum, sent as the add-torrent
/// <c>contentLayout</c> field) plus "use qBittorrent's own setting", which is
/// what an install that never chose gets and is exactly today's behaviour.
/// </summary>
public static class ContentLayouts
{
    public const string Default = "default";
    public const string Original = "original";
    public const string Subfolder = "subfolder";
    public const string NoSubfolder = "no-subfolder";

    /// <summary>Anything unrecognised means "leave it to the client".</summary>
    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "original" => Original,
            "subfolder" or "create-subfolder" => Subfolder,
            "no-subfolder" or "nosubfolder" or "dont-create-subfolder" => NoSubfolder,
            _ => Default
        };

    /// <summary>The value qBittorrent's add call takes, or null when nothing should be sent.</summary>
    public static string? ToQbittorrentValue(string? value)
        => Normalize(value) switch
        {
            Original => "Original",
            Subfolder => "Subfolder",
            NoSubfolder => "NoSubfolder",
            _ => null
        };
}
