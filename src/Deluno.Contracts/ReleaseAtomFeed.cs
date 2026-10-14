using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// Reads a GitHub <c>releases.atom</c> or <c>tags.atom</c> feed (#1181). These pages live on github.com, not on
/// api.github.com, so reading them never spends the 60 calls an hour GitHub gives one network. A draft is never in
/// the feed; a release candidate is.
/// </summary>
public static partial class ReleaseAtomFeed
{
    private const string Marker = "/releases/tag/";

    [GeneratedRegex("<link[^>]*href=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>The release feed's address for a repository ("owner/name").</summary>
    public static Uri ReleasesUri(string repository) => new($"https://github.com/{repository}/releases.atom");

    /// <summary>The tag feed's address for a repository, for projects that only tag.</summary>
    public static Uri TagsUri(string repository) => new($"https://github.com/{repository}/tags.atom");

    /// <summary>The page that tells about one tag.</summary>
    public static string TagPageUrl(string repository, string tag) => $"https://github.com/{repository}{Marker}{Uri.EscapeDataString(tag)}";

    /// <summary>The address one release file is downloaded from. It needs no API call.</summary>
    public static string DownloadUrl(string repository, string tag, string asset)
        => $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";

    /// <summary>The folder a release's files are in, with a closing slash.</summary>
    public static string DownloadFolderUrl(string repository, string tag)
        => $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/";

    /// <summary>
    /// Every tag the feed lists, in feed order. With <paramref name="tagPrefix"/> only tags that start with it.
    /// </summary>
    public static IReadOnlyList<string> Tags(string atom, string tagPrefix = "")
    {
        var tags = new List<string>();
        foreach (Match link in LinkPattern().Matches(atom))
        {
            var href = link.Groups[1].Value;
            var at = href.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var tag = Uri.UnescapeDataString(href[(at + Marker.Length)..]);
            if (tag.Length > 0 && tag.StartsWith(tagPrefix, StringComparison.Ordinal))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>
    /// Every readable tag, newest version first (not feed order), each tag once. Release candidates count when
    /// <paramref name="includePreReleases"/> is true. Used to look at the next release when the newest has no files yet (#1263).
    /// </summary>
    public static IReadOnlyList<string> TagsNewestFirst(string atom, bool includePreReleases = true, string tagPrefix = "")
        => Tags(atom, tagPrefix)
            .Distinct(StringComparer.Ordinal)
            .Select((tag, index) => (tag, index, version: ReleaseVersions.Parse(tag[tagPrefix.Length..])))
            .Where(x => x.version is not null && (includePreReleases || !x.version.IsPreRelease))
            .OrderByDescending(x => x.version!, Comparer<ParsedVersion>.Create(ReleaseVersions.ComparePrecedence))
            .ThenBy(x => x.index)
            .Select(x => x.tag)
            .ToList();

    /// <summary>
    /// The newest tag by version order (not feed order). Release candidates count when
    /// <paramref name="includePreReleases"/> is true. Null when no tag is readable.
    /// </summary>
    public static string? NewestTag(string atom, bool includePreReleases = true, string tagPrefix = "")
    {
        string? bestTag = null;
        ParsedVersion? best = null;
        foreach (var tag in Tags(atom, tagPrefix))
        {
            if (ReleaseVersions.Parse(tag[tagPrefix.Length..]) is not { } version || (!includePreReleases && version.IsPreRelease))
            {
                continue;
            }

            if (best is null || ReleaseVersions.ComparePrecedence(version, best) > 0)
            {
                best = version;
                bestTag = tag;
            }
        }

        return bestTag;
    }
}
