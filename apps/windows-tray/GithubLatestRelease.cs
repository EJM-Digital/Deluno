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
    public static async Task<ResolvedGithubAsset?> ResolveAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.GithubRepo) || string.IsNullOrWhiteSpace(spec.GithubAsset))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno-Installer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var release = await http.GetFromJsonAsync<GithubRelease>(
                $"https://api.github.com/repos/{spec.GithubRepo}/releases/latest", cancellationToken);
            if (release?.Assets is null)
            {
                return null;
            }

            var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubAsset, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null)
            {
                return null;
            }

            string? sha256 = null;
            if (!string.IsNullOrWhiteSpace(spec.GithubChecksumAsset))
            {
                var checksumAsset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubChecksumAsset, StringComparison.OrdinalIgnoreCase));
                if (checksumAsset?.BrowserDownloadUrl is not null)
                {
                    var sums = await http.GetStringAsync(checksumAsset.BrowserDownloadUrl, cancellationToken);
                    sha256 = ParseChecksum(sums, spec.GithubAsset!);
                }
            }

            return new ResolvedGithubAsset(asset.BrowserDownloadUrl, sha256);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a `sha256  filename` line from a standard SHA256SUMS.txt.</summary>
    public static string? ParseChecksum(string sumsFileContent, string fileName)
    {
        foreach (var line in sumsFileContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var name = parts[^1].TrimStart('*');
            if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }
        return null;
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
