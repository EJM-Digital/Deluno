using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

public sealed record ResolvedGithubAsset(string AssetUrl, string? Sha256);

/// <summary>
/// What a lookup found. <see cref="RateLimited"/> is GitHub's own limit on anonymous calls (60 an hour for
/// everything behind one public address): not a missing release, so it is told apart and says when it resets.
/// </summary>
public sealed record GithubLookup(
    ResolvedGithubAsset? Asset,
    bool RateLimited = false,
    DateTimeOffset? LimitResetsUtc = null,
    bool FeedNotFound = false,
    string? Tag = null,
    IReadOnlyList<string>? Skipped = null,
    bool NoInstallerYet = false)
{
    /// <summary>The install-log line naming the release used and why newer ones were skipped; null when the newest was used.</summary>
    public string? ChoiceLogLine(string displayName)
        => Asset is null || Skipped is not { Count: > 0 } ? null : $"{displayName}: using {Tag}; newer releases were skipped ({string.Join("; ", Skipped)})";
}

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
        => (await LookupAsync(spec, cancellationToken, handler)).Asset;

    /// <summary>
    /// Plain words for a lookup that GitHub's limit stopped: the one sentence the server says too
    /// (<see cref="Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor"/>), with when it lifts in this computer's own time.
    /// </summary>
    public static string RateLimitMessage(DateTimeOffset resetsUtc, TimeZoneInfo? zone = null)
        => Deluno.Infrastructure.Http.GitHubRateLimitException.LocalizeResetTimes(
            Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor(resetsUtc), zone);

    /// <summary>The release feed answered 404: the repository is private or does not exist, and the API would say the same (#1181).</summary>
    private sealed class FeedNotFoundException : Exception;

    /// <summary>Plain words for a repository whose release feed answers 404.</summary>
    public static string FeedNotFoundMessage(string displayName)
        => $"Couldn't check for {displayName}'s latest release: the release feed isn't reachable (GitHub says it isn't there, so the repository may be private).";

    /// <summary>Plain words for a repository whose newest releases have no installer (or no checksum for it) uploaded yet.</summary>
    public static string NoInstallerYetMessage(string displayName)
        => $"{displayName}'s newest releases have no Windows installer yet; try again in a few minutes.";

    private static async Task<string> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// How many of a repository's newest releases the picker looks at before it says none has its installer yet (#1263).
    /// The picker's anonymous budget is about six calls: the release feed (or the one list of releases), then one
    /// checksum file per release looked at. Five is enough because a release that is still being published has
    /// no files for minutes, not for five releases, and a person who hits it is told to try again.
    /// </summary>
    public const int MaxReleasesLooked = 5;

    public static async Task<GithubLookup> LookupAsync(BundledComponentSpec spec, CancellationToken cancellationToken, HttpMessageHandler? handler = null, Deluno.Infrastructure.Http.GitHubApiState? apiState = null, TimeProvider? time = null, IReadOnlyCollection<string>? excludeTags = null)
    {
        var hasPattern = !string.IsNullOrWhiteSpace(spec.GithubAssetPattern);
        if (string.IsNullOrWhiteSpace(spec.GithubRepo) || (string.IsNullOrWhiteSpace(spec.GithubAsset) && !hasPattern))
        {
            return new GithubLookup(null);
        }

        try
        {
            // Every api.github.com call goes through the shared conditional handler (ETag, 304, the limit remembered).
            using var http = GithubApiClient.Create(handler, "Deluno-Installer", TimeSpan.FromSeconds(30), apiState, time);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // The free route first (#1181): the newest tags from github.com's own release feed and the files from
            // the release download folder, neither of which counts against GitHub's 60 API calls an hour. It needs
            // the asset names to be exact, so a component whose file name carries its version (a pattern) uses
            // the API as before.
            if (!hasPattern && string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern))
            {
                var fromFeed = await ResolveFromFeedAsync(http, spec, cancellationToken);
                if (fromFeed is not null)
                {
                    return fromFeed;
                }
            }

            return await ResolveFromApiAsync(http, spec, hasPattern, cancellationToken);
        }
        catch (Deluno.Infrastructure.Http.GitHubRateLimitException limited)
        {
            return new GithubLookup(null, RateLimited: true, LimitResetsUtc: limited.ResetsAt);
        }
        catch (FeedNotFoundException)
        {
            return new GithubLookup(null, FeedNotFound: true);
        }
        catch
        {
            return new GithubLookup(null);
        }
    }

    private static string InstallerName(BundledComponentSpec spec) => string.IsNullOrWhiteSpace(spec.GithubAsset) ? "its Windows installer" : spec.GithubAsset!;

    private static string ChecksumName(BundledComponentSpec spec) => string.IsNullOrWhiteSpace(spec.GithubChecksumAsset) ? "its checksum file" : spec.GithubChecksumAsset!;

    private static bool NeedsChecksum(BundledComponentSpec spec)
        => !string.IsNullOrWhiteSpace(spec.GithubChecksumAsset) || !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern);

    /// <summary>
    /// The installer and its checksum found without the API: the newest tags (release candidates only for a
    /// component that publishes them, drafts never) from <c>releases.atom</c>, then the files from
    /// <c>releases/download/&lt;tag&gt;/</c>. A release whose checksum file is not there yet (a release is published
    /// before its files finish uploading, #1263), or whose checksum file does not list the installer, is skipped for
    /// the next newest, up to <see cref="MaxReleasesLooked"/>. Null when the feed cannot be read or a call fails for
    /// any reason but "not there", so the caller falls back to the API.
    /// </summary>
    private static async Task<GithubLookup?> ResolveFromFeedAsync(HttpClient http, BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(Deluno.Contracts.ReleaseAtomFeed.ReleasesUri(spec.GithubRepo!), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new FeedNotFoundException();
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var tags = Deluno.Contracts.ReleaseAtomFeed.TagsNewestFirst(await response.Content.ReadAsStringAsync(cancellationToken), spec.IncludePreReleases);
            if (tags.Count == 0)
            {
                return null;
            }

            var skipped = new List<string>();
            foreach (var tag in tags.Take(MaxReleasesLooked))
            {
                var assetUrl = Deluno.Contracts.ReleaseAtomFeed.DownloadUrl(spec.GithubRepo!, tag, spec.GithubAsset!);
                string? sha256 = null;
                if (!string.IsNullOrWhiteSpace(spec.GithubChecksumAsset))
                {
                    using var sums = await http.GetAsync(Deluno.Contracts.ReleaseAtomFeed.DownloadUrl(spec.GithubRepo!, tag, spec.GithubChecksumAsset), cancellationToken);
                    if (sums.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        skipped.Add($"{tag} has no {spec.GithubChecksumAsset} yet");
                        continue;
                    }

                    if (!sums.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    sha256 = ParseChecksum(await sums.Content.ReadAsStringAsync(cancellationToken), spec.GithubAsset!);
                    if (sha256 is null)
                    {
                        skipped.Add($"{tag}'s {spec.GithubChecksumAsset} does not list {spec.GithubAsset}");
                        continue;
                    }
                }
                else
                {
                    // Without a checksum to read, make sure the file is really in that release before saying so.
                    using var head = new HttpRequestMessage(HttpMethod.Head, assetUrl);
                    using var exists = await http.SendAsync(head, cancellationToken);
                    if (exists.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        skipped.Add($"{tag} has no {spec.GithubAsset} yet");
                        continue;
                    }

                    if (!exists.IsSuccessStatusCode)
                    {
                        return null;
                    }
                }

                return new GithubLookup(new ResolvedGithubAsset(assetUrl, sha256), Tag: tag, Skipped: skipped);
            }

            return new GithubLookup(null, Skipped: skipped, NoInstallerYet: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The same walk through the API's release list, for a component whose file names carry their version (a pattern) or
    /// when the feed could not be read. The assets of every release arrive in the one list, so only the checksum file of
    /// a release that has the installer costs another call.
    /// Weir alone may be a pre-release (#1123): it is published as release candidates, so its releases are taken in
    /// version order, drafts never. Every other component starts from GitHub's own "latest release", which is the
    /// newest stable one and never a pre-release, and only reads the list when that release is not complete.
    /// </summary>
    private static async Task<GithubLookup> ResolveFromApiAsync(HttpClient http, BundledComponentSpec spec, bool hasPattern, CancellationToken cancellationToken)
    {
        var skipped = new List<string>();
        var looked = 0;
        var triedTag = new HashSet<string>(StringComparer.Ordinal);

        if (!spec.IncludePreReleases)
        {
            var latestJson = await GetJsonAsync(http, $"https://api.github.com/repos/{spec.GithubRepo}/releases/latest", cancellationToken);
            var latest = System.Text.Json.JsonSerializer.Deserialize<GithubRelease>(latestJson);
            if (latest is not null)
            {
                looked++;
                if (latest.TagName is not null)
                {
                    triedTag.Add(latest.TagName);
                }

                var found = await TryReleaseAsync(http, spec, hasPattern, latest, skipped, cancellationToken);
                if (found is not null)
                {
                    return new GithubLookup(found, Tag: latest.TagName, Skipped: skipped);
                }
            }
        }

        var releasesJson = await GetJsonAsync(http, $"https://api.github.com/repos/{spec.GithubRepo}/releases?per_page=100", cancellationToken);
        foreach (var element in Deluno.Contracts.ReleaseVersions.GitHubReleasesNewestFirst(releasesJson, spec.IncludePreReleases))
        {
            if (looked >= MaxReleasesLooked)
            {
                break;
            }

            var release = System.Text.Json.JsonSerializer.Deserialize<GithubRelease>(element.GetRawText());
            if (release is null || (release.TagName is not null && triedTag.Contains(release.TagName)))
            {
                continue;
            }

            looked++;
            var found = await TryReleaseAsync(http, spec, hasPattern, release, skipped, cancellationToken);
            if (found is not null)
            {
                return new GithubLookup(found, Tag: release.TagName, Skipped: skipped);
            }
        }

        // Nothing readable at all is a repository with no releases; releases that were all incomplete are "not yet".
        return skipped.Count == 0 ? new GithubLookup(null) : new GithubLookup(null, Skipped: skipped, NoInstallerYet: true);
    }

    /// <summary>
    /// The installer of one release with its checksum read from the release's own checksum file, or null with the reason
    /// added to <paramref name="skipped"/>. A component that publishes a checksum file is never installed from a release
    /// without it: the file is checked against that release's own checksum, exactly as before.
    /// </summary>
    private static async Task<ResolvedGithubAsset?> TryReleaseAsync(HttpClient http, BundledComponentSpec spec, bool hasPattern, GithubRelease release, List<string> skipped, CancellationToken cancellationToken)
    {
        var label = release.TagName ?? "A release";
        var assets = release.Assets ?? [];

        // A pattern (for a vendor whose asset name embeds the version, e.g.
        // NZBGet's nzbget-21.1-bin-windows-setup.exe) wins over an exact
        // name so an upgrade never silently stops matching.
        var asset = hasPattern
            ? assets.FirstOrDefault(a => a.Name is not null && System.Text.RegularExpressions.Regex.IsMatch(a.Name, spec.GithubAssetPattern!))
            : assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubAsset, StringComparison.OrdinalIgnoreCase));
        if (asset?.BrowserDownloadUrl is null || asset.Name is null)
        {
            skipped.Add($"{label} has no {InstallerName(spec)} yet");
            return null;
        }

        string? sha256 = null;
        if (NeedsChecksum(spec))
        {
            // Same pattern-wins-over-exact-name rule as the installer
            // asset itself: NZBGet's own checksum file name also embeds
            // the version (nzbget-21.1.sig.txt), so a fixed name would
            // stop matching on the next release.
            var hasChecksumPattern = !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern);
            var checksumAsset = hasChecksumPattern
                ? assets.FirstOrDefault(a => a.Name is not null && System.Text.RegularExpressions.Regex.IsMatch(a.Name, spec.GithubChecksumAssetPattern!))
                : assets.FirstOrDefault(a => string.Equals(a.Name, spec.GithubChecksumAsset, StringComparison.OrdinalIgnoreCase));
            if (checksumAsset?.BrowserDownloadUrl is null)
            {
                skipped.Add($"{label} has no {ChecksumName(spec)} yet");
                return null;
            }

            using var response = await http.GetAsync(checksumAsset.BrowserDownloadUrl, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                skipped.Add($"{label} has no {ChecksumName(spec)} yet");
                return null;
            }

            response.EnsureSuccessStatusCode();
            sha256 = ParseChecksum(await response.Content.ReadAsStringAsync(cancellationToken), asset.Name);
            if (sha256 is null)
            {
                skipped.Add($"{label}'s {checksumAsset.Name ?? ChecksumName(spec)} does not list {asset.Name}");
                return null;
            }
        }

        return new ResolvedGithubAsset(asset.BrowserDownloadUrl, sha256);
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
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

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
