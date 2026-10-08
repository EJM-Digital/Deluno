using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

public sealed record ResolvedGithubAsset(string AssetUrl, string? Sha256);

/// <summary>
/// Resolves Weir's latest Windows installer straight from its own GitHub
/// releases, rather than a version pinned in the manifest (#640). Weir is
/// Deluno's sibling product and auto-updates itself once installed, so
/// "whatever the vendor currently publishes" is the correct answer for it in
/// a way it deliberately is not for the five third-party download clients
/// (see bundled-components.json's own note on why those are pinned).
///
/// <para>Every Weir release also publishes <c>SHA256SUMS.txt</c> alongside
/// <c>Weir-win-Setup.exe</c>, so the asset is checked against Weir's own
/// published checksum instead of one this repository would have to keep
/// updated by hand.</para>
/// </summary>
public static class GithubLatestRelease
{
    public static async Task<ResolvedGithubAsset?> ResolveAsync(BundledComponentSpec spec, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        var hasPattern = !string.IsNullOrWhiteSpace(spec.GithubAssetPattern);
        if (string.IsNullOrWhiteSpace(spec.GithubRepo) || (string.IsNullOrWhiteSpace(spec.GithubAsset) && !hasPattern))
        {
            return null;
        }

        try
        {
            using var http = handler is null ? new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno-Installer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // Weir alone may be a pre-release (#1123): it is published as release candidates, so its newest
            // release by version order is taken, drafts never. Every other component keeps GitHub's own
            // "latest release", which is the newest stable one and never a pre-release.
            GithubRelease? release;
            if (spec.IncludePreReleases)
            {
                var releasesJson = await http.GetStringAsync(
                    $"https://api.github.com/repos/{spec.GithubRepo}/releases?per_page=100", cancellationToken);
                release = PickRelease(releasesJson);
            }
            else
            {
                var latestJson = await http.GetStringAsync(
                    $"https://api.github.com/repos/{spec.GithubRepo}/releases/latest", cancellationToken);
                release = System.Text.Json.JsonSerializer.Deserialize<GithubRelease>(latestJson);
            }
            if (release?.Assets is null)
            {
                return null;
            }

            // A pattern (for a vendor whose asset name embeds the version, e.g.
            // NZBGet's nzbget-21.1-bin-windows-setup.exe) wins over an exact
            // name so an upgrade never silently stops matching.
            var asset = hasPattern
                ? release.Assets.FirstOrDefault(a => a.Name is not null && System.Text.RegularExpressions.Regex.IsMatch(a.Name, spec.GithubAssetPattern!))
                : release.Assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubAsset, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null || asset.Name is null)
            {
                return null;
            }

            string? sha256 = null;
            var hasChecksumPattern = !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern);
            if (!string.IsNullOrWhiteSpace(spec.GithubChecksumAsset) || hasChecksumPattern)
            {
                // Same pattern-wins-over-exact-name rule as the installer
                // asset itself: NZBGet's own checksum file name also embeds
                // the version (nzbget-21.1.sig.txt), so a fixed name would
                // stop matching on the next release.
                var checksumAsset = hasChecksumPattern
                    ? release.Assets.FirstOrDefault(a => a.Name is not null && System.Text.RegularExpressions.Regex.IsMatch(a.Name, spec.GithubChecksumAssetPattern!))
                    : release.Assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubChecksumAsset, StringComparison.OrdinalIgnoreCase));
                if (checksumAsset?.BrowserDownloadUrl is not null)
                {
                    var sums = await http.GetStringAsync(checksumAsset.BrowserDownloadUrl, cancellationToken);
                    sha256 = ParseChecksum(sums, asset.Name);
                }
            }

            return new ResolvedGithubAsset(asset.BrowserDownloadUrl, sha256);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The newest release in a GitHub <c>/releases</c> answer: highest version, pre-releases included, drafts never.</summary>
    public static GithubRelease? PickRelease(string releasesJson)
        => Deluno.Contracts.ReleaseVersions.NewestGitHubRelease(releasesJson) is { } newest
            ? System.Text.Json.JsonSerializer.Deserialize<GithubRelease>(newest.GetRawText())
            : null;

    /// <summary>Reads a `sha256  filename` line from a standard SHA256SUMS.txt, falling back to NZBGet's own per-release signature file shape (see <see cref="ParseJsonpSignature"/>) when no line matches.</summary>
    public static string? ParseChecksum(string sumsFileContent, string fileName)
    {
        foreach (var line in sumsFileContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            // Strip the optional leading "*" (binary-mode marker in a standard
            // SHA256SUMS file) and any path prefix, then match the file name
            // exactly. EndsWith alone would let "OldWeir-win-Setup.exe" satisfy
            // a lookup for "Weir-win-Setup.exe" -- a real risk here, since Weir
            // resolves and verifies whatever its latest GitHub release names,
            // not a name pinned in this repo.
            var name = parts[^1].TrimStart('*');
            var slash = name.LastIndexOfAny(['/', '\\']);
            var baseName = slash >= 0 ? name[(slash + 1)..] : name;
            if (string.Equals(baseName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return ParseJsonpSignature(sumsFileContent, fileName);
    }

    /// <summary>
    /// Reads a SHA-256 hex digest out of NZBGet's own per-release
    /// <c>nzbget-&lt;version&gt;.sig.txt</c>, a JSONP body shaped like
    /// <c>nzbget_signatures({ "SHA256(nzbget-21.1-bin-windows-setup.exe)" : "&lt;hash&gt;", ... });</c>
    /// rather than a plain SHA256SUMS.txt line -- not every vendor's
    /// "checksums with the release" publishes them in the same shape.
    /// </summary>
    private static string? ParseJsonpSignature(string content, string fileName)
    {
        var pattern = $"\"SHA256\\({System.Text.RegularExpressions.Regex.Escape(fileName)}\\)\"\\s*:\\s*\"([0-9a-fA-F]{{64}})\"";
        var match = System.Text.RegularExpressions.Regex.Match(content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public sealed class GithubRelease
    {
        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    public sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
