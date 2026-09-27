using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>Whether the content blocklist's patterns name what to block, or what to allow.</summary>
public static class ContentBlocklistModes
{
    public const string Blacklist = "blacklist";
    public const string Whitelist = "whitelist";

    public static readonly IReadOnlyList<string> All = [Blacklist, Whitelist];

    public static bool IsKnown(string mode) => All.Contains(mode, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The built-in pattern set Deluno ships (#816), equivalent in spirit to
/// Cleanuparr's official curated list: risky executable, script and shortcut
/// extensions that have no place inside a video release.
/// </summary>
public static class ContentBlocklistDefaults
{
    public static readonly IReadOnlyList<string> BuiltInPatterns =
    [
        "*.exe", "*.scr", "*.bat", "*.cmd", "*.com", "*.pif", "*.vbs", "*.vbe",
        "*.js", "*.jse", "*.wsf", "*.wsh", "*.msi", "*.msp", "*.ps1", "*.ps1xml",
        "*.psc1", "*.lnk", "*.url", "*.jar", "*.reg", "*.hta", "*.scf", "*.chm",
        "*.gadget", "*.application", "autorun.inf"
    ];

    /// <summary>How often a configured custom remote URL is re-fetched (#816). Deluno picks one interval rather than matching Cleanuparr's two-tier timing.</summary>
    public static readonly TimeSpan RemoteRefreshInterval = TimeSpan.FromHours(4);
}

/// <summary>
/// One installation's content-blocklist configuration (#816): whether the
/// per-file check runs at all, which pattern set it checks against, and the
/// last remote fetch, if a custom URL is configured. Off by default (#936) -
/// checking every file inside every download is new work Deluno does not
/// perform until asked to.
/// </summary>
public sealed record ContentBlocklistSettings(
    bool Enabled,
    string Mode,
    bool UseBuiltInPatterns,
    IReadOnlyList<string> CustomPatterns,
    string? RemoteUrl,
    IReadOnlyList<string> RemotePatterns,
    DateTimeOffset? LastRefreshedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// #822: pushes this blocklist into every enabled qBittorrent client's own
    /// "Excluded file names" preference, hourly, without turning that qBittorrent
    /// setting on itself. Off by default (#936) and independent of
    /// <see cref="Enabled"/> - a person can check files without ever touching
    /// qBittorrent's own configuration, and can sync into qBittorrent without
    /// Deluno's own per-file check running at all.
    /// </summary>
    bool SyncToQbittorrentEnabled = false)
{
    public static readonly ContentBlocklistSettings Default = new(
        Enabled: false,
        Mode: ContentBlocklistModes.Blacklist,
        UseBuiltInPatterns: true,
        CustomPatterns: [],
        RemoteUrl: null,
        RemotePatterns: [],
        LastRefreshedUtc: null,
        UpdatedUtc: DateTimeOffset.MinValue,
        SyncToQbittorrentEnabled: false);

    /// <summary>Every pattern currently in effect: built-in (if kept), hand-entered custom patterns, and whatever the last remote fetch returned.</summary>
    public IReadOnlyList<string> EffectivePatterns()
        => (UseBuiltInPatterns ? ContentBlocklistDefaults.BuiltInPatterns : [])
            .Concat(CustomPatterns ?? [])
            .Concat(RemotePatterns ?? [])
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Matches the files actually inside a download against a content blocklist
/// (#816) - a glob-style check against each file's own name, not the release
/// name, so a dangerous file packaged inside an otherwise normal-looking
/// release is caught.
/// </summary>
public static class ContentBlocklistMatcher
{
    /// <summary>
    /// The first file that trips the blocklist, or null when none does.
    /// Blacklist mode: the first file matching any pattern. Whitelist mode:
    /// the first file matching no pattern at all (nothing on the allow list
    /// covers it).
    /// </summary>
    public static string? FindOffendingFile(IReadOnlyList<string> files, ContentBlocklistSettings settings)
    {
        if (files.Count == 0) return null;
        var patterns = settings.EffectivePatterns();
        if (patterns.Count == 0) return null;

        return string.Equals(settings.Mode, ContentBlocklistModes.Whitelist, StringComparison.OrdinalIgnoreCase)
            ? files.FirstOrDefault(file => !patterns.Any(pattern => IsMatch(pattern, file)))
            : files.FirstOrDefault(file => patterns.Any(pattern => IsMatch(pattern, file)));
    }

    /// <summary>
    /// A simple glob match (<c>*</c> and <c>?</c>), case-insensitive, against
    /// the file's own name - a full path is trimmed to its last segment first,
    /// so a pattern like <c>*.exe</c> matches regardless of which folder
    /// inside the torrent the file sits in.
    /// </summary>
    public static bool IsMatch(string pattern, string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
        var fileName = fileNameOrPath.Replace('\\', '/').Split('/').Last();
        var regexPattern = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
