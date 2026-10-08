using System.Net.Http;
using System.Text.RegularExpressions;

namespace Deluno.Tray;

/// <summary>
/// Resolves the latest Windows installer for a vendor (Deluge) that publishes
/// no GitHub Releases at all -- <see cref="GithubLatestRelease"/> does not
/// apply, since <c>deluge-torrent/deluge</c> has zero releases in its API,
/// only tags. Deluge's own Windows build instead lands in a plain Apache
/// directory listing at <c>https://ftp.osuosl.org/pub/deluge/windows/</c>
/// (the same host <c>download.deluge-torrent.org</c> serves from), one file
/// per version, e.g. <c>deluge-2.2.0-win64-setup.exe</c> next to its own
/// <c>deluge-2.2.0-win64-setup.exe.sha256</c> -- a "hash  filename" line
/// exactly like a GitHub SHA256SUMS.txt entry, so <see
/// cref="GithubLatestRelease.ParseChecksum"/> reads it too.
/// </summary>
public static class DirectoryListingLatestRelease
{
    private static readonly Regex HrefPattern = new("href=\"(?<name>[^\"?/]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"(?<version>\d+(?:\.\d+)+)", RegexOptions.Compiled);

    public static async Task<ResolvedGithubAsset?> ResolveAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.DirectoryListingUrl) || string.IsNullOrWhiteSpace(spec.AssetPattern))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno-Installer");

            var html = await http.GetStringAsync(spec.DirectoryListingUrl, cancellationToken);
            var fileName = PickLatest(html, spec.AssetPattern);
            if (fileName is null)
            {
                return null;
            }

            var assetUrl = new Uri(new Uri(spec.DirectoryListingUrl), fileName).ToString();

            string? sha256 = null;
            if (!string.IsNullOrWhiteSpace(spec.ChecksumSuffix))
            {
                try
                {
                    var sums = await http.GetStringAsync(assetUrl + spec.ChecksumSuffix, cancellationToken);
                    sha256 = GithubLatestRelease.ParseChecksum(sums, fileName);
                }
                catch
                {
                    // No checksum file for this particular release: the
                    // caller falls back to the manifest's own MustBeSigned
                    // requirement (BundledComponentsTests enforces every
                    // component has at least one verification path).
                }
            }

            return new ResolvedGithubAsset(assetUrl, sha256);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the highest-versioned file name matching <paramref name="pattern"/>
    /// among the listing's own links, by parsed version number rather than
    /// listing order -- a directory listing is not guaranteed sorted the same
    /// way every time it is fetched (e.g. by name vs. by date).
    /// </summary>
    public static string? PickLatest(string directoryListingHtml, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        string? best = null;
        Version? bestVersion = null;

        foreach (Match link in HrefPattern.Matches(directoryListingHtml))
        {
            var name = link.Groups["name"].Value;
            if (!regex.IsMatch(name))
            {
                continue;
            }

            var versionMatch = VersionPattern.Match(name);
            var version = versionMatch.Success && Version.TryParse(versionMatch.Groups["version"].Value, out var parsed)
                ? parsed
                : new Version(0, 0);

            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                best = name;
            }
        }

        return best;
    }
}
