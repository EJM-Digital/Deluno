using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

public sealed class GithubLatestReleaseTests
{
    private const string Releases = """
        [{"tag_name":"v1.0.0-rc.1","draft":false,"prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/rc1/Weir-win-Setup.exe"}]},
         {"tag_name":"v1.0.0-rc.2","draft":false,"prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/rc2/Weir-win-Setup.exe"},{"name":"SHA256SUMS.txt","browser_download_url":"https://x.test/rc2/SHA256SUMS.txt"}]},
         {"tag_name":"v9.0.0","draft":true,"prerelease":false,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/draft/Weir-win-Setup.exe"}]}]
        """;

    [Fact]
    public void Picks_the_highest_version_release_including_pre_releases_and_never_a_draft()
    {
        var release = GithubLatestRelease.PickRelease(Releases);

        Assert.Equal("https://x.test/rc2/Weir-win-Setup.exe", release!.Assets!.First(a => a.Name == "Weir-win-Setup.exe").BrowserDownloadUrl);
        Assert.Contains(release.Assets!, a => a.Name == "SHA256SUMS.txt");
    }

    [Fact]
    public void Resolves_a_repository_whose_only_release_is_a_pre_release()
    {
        var release = GithubLatestRelease.PickRelease("""[{"tag_name":"v1.0.0-rc.1","prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/Weir-win-Setup.exe"}]}]""");

        Assert.Equal("https://x.test/Weir-win-Setup.exe", release!.Assets![0].BrowserDownloadUrl);
    }

    [Fact]
    public void Orders_rc2_over_rc1_and_a_stable_release_over_rc9()
    {
        string Pick(params string[] tags) => GithubLatestRelease.PickRelease("[" + string.Join(",", tags.Select(t => $$"""{"tag_name":"{{t}}","assets":[{"name":"{{t}}","browser_download_url":"https://x.test/{{t}}"}]}""")) + "]")!.Assets![0].Name!;

        Assert.Equal("v1.0.0-rc.2", Pick("v1.0.0-rc.1", "v1.0.0-rc.2"));
        Assert.Equal("v1.0.0", Pick("v1.0.0-rc.9", "v1.0.0"));
        Assert.Null(GithubLatestRelease.PickRelease("""[{"tag_name":"v1.0.0","draft":true}]"""));
    }

    [Fact]
    public void The_checksum_check_still_applies_to_the_pre_release_asset()
    {
        var release = GithubLatestRelease.PickRelease(Releases)!;
        var sums = "d34db33f  Weir-win-Setup.exe\n";

        Assert.Equal("d34db33f", GithubLatestRelease.ParseChecksum(sums, release.Assets!.First(a => a.Name == "Weir-win-Setup.exe").Name!));
        Assert.Null(GithubLatestRelease.ParseChecksum("d34db33f  OldWeir-win-Setup.exe\n", "Weir-win-Setup.exe"));
    }

    [Fact]
    public void Reads_the_matching_line_from_a_standard_SHA256SUMS_file()
    {
        var sums = "abc123  RELEASES\n" +
                   "d34db33f00000000000000000000000000000000000000000000000000000000  Weir-win-Setup.exe\n" +
                   "cafef00d  weir-web-dist.zip\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Equal("d34db33f00000000000000000000000000000000000000000000000000000000", hash);
    }

    [Fact]
    public void Answers_null_when_the_file_has_no_matching_line()
    {
        var hash = GithubLatestRelease.ParseChecksum("abc123  something-else.exe\n", "Weir-win-Setup.exe");

        Assert.Null(hash);
    }

    [Fact]
    public void Does_not_match_a_line_whose_name_merely_ends_with_the_target_file_name()
    {
        // "OldWeir-win-Setup.exe" ends with "Weir-win-Setup.exe" but is not it.
        // A pure EndsWith match would wrongly hand back this line's hash for a
        // lookup of the real asset.
        var sums = "deadbeef  OldWeir-win-Setup.exe\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Null(hash);
    }

    [Fact]
    public void Matches_a_path_prefixed_entry_by_its_base_name()
    {
        var sums = "d34db33f  dist/Weir-win-Setup.exe\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Equal("d34db33f", hash);
    }

    [Fact]
    public void Reads_a_hash_out_of_nzbgets_own_jsonp_signature_file()
    {
        // NZBGet's nzbget-<version>.sig.txt is not a SHA256SUMS.txt line --
        // it is a JSONP body naming every asset's SHA256 as its own key.
        var sig = "nzbget_signatures({\n" +
                  "\"MD5(nzbget-21.1-bin-windows-setup.exe)\" : \"4298f1eec6d7c8644844a28ceb92c0ed\",\n" +
                  "\"SHA1(nzbget-21.1-bin-windows-setup.exe)\" : \"61723cec5870438763d21bb7259a76a631c8108f\",\n" +
                  "\"SHA256(nzbget-21.1-bin-windows-setup.exe)\" : \"536515ca1e57321c5de664441d3394c4cd63c603183e257275188b3a9e168477\",\n" +
                  "\"RSA-SHA256(nzbget-21.1-bin-windows-setup.exe)\" : \"deadbeef\"\n" +
                  "});";

        var hash = GithubLatestRelease.ParseChecksum(sig, "nzbget-21.1-bin-windows-setup.exe");

        Assert.Equal("536515ca1e57321c5de664441d3394c4cd63c603183e257275188b3a9e168477", hash);
    }

    [Fact]
    public void Jsonp_signature_lookup_answers_null_for_a_different_asset_name()
    {
        var sig = "nzbget_signatures({\n" +
                  "\"SHA256(nzbget-21.1-bin-linux.run)\" : \"9c4bd5169c91c4ad56338d0989d63632abe708bdf496137b484e3faa18708011\"\n" +
                  "});";

        var hash = GithubLatestRelease.ParseChecksum(sig, "nzbget-21.1-bin-windows-setup.exe");

        Assert.Null(hash);
    }
}

public sealed class DirectoryListingLatestReleaseTests
{
    [Fact]
    public void Picks_the_highest_version_among_matching_links_regardless_of_listing_order()
    {
        var html = "<a href=\"deluge-2.1.1-win64-lt2.0-setup.exe\">deluge-2.1.1-win64-lt2.0-setup.exe</a>\n" +
                    "<a href=\"deluge-2.2.0-win64-setup.exe\">deluge-2.2.0-win64-setup.exe</a>\n" +
                    "<a href=\"deluge-2.1.0-win64-setup.exe\">deluge-2.1.0-win64-setup.exe</a>\n" +
                    "<a href=\"deluge-2.2.0-win32-setup.exe\">deluge-2.2.0-win32-setup.exe</a>\n";

        var picked = DirectoryListingLatestRelease.PickLatest(html, @"^deluge-[0-9][0-9.]*-win64-setup\.exe$");

        // Not the win32 build (wrong pattern), not the lt2.0 compatibility
        // build (excluded by the pattern), and not 2.1.0 (older version).
        Assert.Equal("deluge-2.2.0-win64-setup.exe", picked);
    }

    [Fact]
    public void Answers_null_when_nothing_in_the_listing_matches()
    {
        var html = "<a href=\"deluge-2.2.0-win32-setup.exe\">deluge-2.2.0-win32-setup.exe</a>\n";

        var picked = DirectoryListingLatestRelease.PickLatest(html, @"^deluge-[0-9][0-9.]*-win64-setup\.exe$");

        Assert.Null(picked);
    }

    // #1123: pre-releases are for Weir only. A component without the flag keeps GitHub's own "latest release".
    private sealed class FakeGitHub(string stable, string all) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal) ? stable : all;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private const string Stable = """{"tag_name":"v21.1","prerelease":false,"assets":[{"name":"nzbget-21.1-bin-windows-setup.exe","browser_download_url":"https://x.test/21.1/nzbget-21.1-bin-windows-setup.exe"}]}""";
    private const string StableAndNewerPreRelease = """
        [{"tag_name":"v22.0-testing","prerelease":true,"assets":[{"name":"nzbget-22.0-bin-windows-setup.exe","browser_download_url":"https://x.test/22.0/nzbget-22.0-bin-windows-setup.exe"}]},
         {"tag_name":"v21.1","prerelease":false,"assets":[{"name":"nzbget-21.1-bin-windows-setup.exe","browser_download_url":"https://x.test/21.1/nzbget-21.1-bin-windows-setup.exe"}]}]
        """;

    [Fact]
    public async Task A_component_without_the_flag_takes_the_latest_stable_release_even_when_a_newer_pre_release_exists()
    {
        var nzbget = BundledComponents.Load().Single(s => s.Kind == "nzbget");
        Assert.False(nzbget.IncludePreReleases);
        var github = new FakeGitHub(Stable, StableAndNewerPreRelease);

        var resolved = await GithubLatestRelease.ResolveAsync(nzbget, CancellationToken.None, github);

        Assert.Equal("https://x.test/21.1/nzbget-21.1-bin-windows-setup.exe", resolved!.AssetUrl);
        Assert.Single(github.Requests);
        Assert.EndsWith("/releases/latest", github.Requests[0]);
    }

    [Fact]
    public async Task Weir_takes_the_newest_release_including_a_pre_release()
    {
        var weir = BundledComponents.Load().Single(s => s.Kind == "weir");
        Assert.True(weir.IncludePreReleases);
        var github = new FakeGitHub(
            """{"tag_name":"v2.0.0","assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/2.0.0/Weir-win-Setup.exe"}]}""",
            """
            [{"tag_name":"v2.1.0-rc.1","prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/2.1.0-rc.1/Weir-win-Setup.exe"}]},
             {"tag_name":"v2.0.0","prerelease":false,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/2.0.0/Weir-win-Setup.exe"}]}]
            """);

        var resolved = await GithubLatestRelease.ResolveAsync(weir, CancellationToken.None, github);

        Assert.Equal("https://x.test/2.1.0-rc.1/Weir-win-Setup.exe", resolved!.AssetUrl);
    }

    // #1181: the free route first. The newest tag comes from github.com's release feed and the files from the release
    // download folder; neither counts against GitHub's 60 API calls an hour.
    private sealed class FeedOnly : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (request.RequestUri.Host == "api.github.com")
            {
                throw new InvalidOperationException("the API must not be called");
            }

            var content = url.EndsWith("/releases.atom", StringComparison.Ordinal)
                ? """<feed><entry><link href="https://github.com/o/weir/releases/tag/v2.0.0"/></entry><entry><link href="https://github.com/o/weir/releases/tag/v2.1.0-rc.1"/></entry></feed>"""
                : url.Contains("SHA256", StringComparison.OrdinalIgnoreCase) || url.Contains("SUMS", StringComparison.OrdinalIgnoreCase)
                    ? "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd  Weir-win-Setup.exe\n"
                    : "installer";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }

    [Fact]
    public async Task Weir_is_found_through_the_release_feed_release_candidates_included_without_the_api()
    {
        var weir = BundledComponents.Load().Single(s => s.Kind == "weir") with { GithubRepo = "o/weir" };
        var handler = new FeedOnly();

        var resolved = await GithubLatestRelease.ResolveAsync(weir, CancellationToken.None, handler);

        Assert.Equal("https://github.com/o/weir/releases/download/v2.1.0-rc.1/Weir-win-Setup.exe", resolved!.AssetUrl);
        Assert.Equal("abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd", resolved.Sha256);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_Weir_includes_pre_releases_in_the_shipped_manifest()
    {
        var flagged = BundledComponents.Load().Where(s => s.IncludePreReleases).Select(s => s.Kind).ToArray();

        Assert.Equal(["weir"], flagged);
    }
}

// Golden path, rc.30.8: GitHub's anonymous limit (60 calls an hour per public address) stopped NZBGet and Weir, and the
// picker said the release could not be found. The limit is now told apart, with when it lifts.
public sealed class GithubRateLimitTests
{
    private sealed class Limited(System.Net.HttpStatusCode status, params (string Name, string Value)[] headers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }

    private static BundledComponentSpec Nzbget() => BundledComponents.Load().Single(s => s.Kind == "nzbget");

    [Fact]
    public async Task A_403_with_no_calls_left_is_the_rate_limit_and_carries_its_reset_time()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(37).ToUnixTimeSeconds();
        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None,
            new Limited(System.Net.HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToString())));

        Assert.True(lookup.RateLimited);
        Assert.Null(lookup.Asset);
        Assert.Equal(reset, lookup.LimitResetsUtc!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_429_with_a_retry_after_is_the_rate_limit_too()
    {
        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None,
            new Limited((System.Net.HttpStatusCode)429, ("Retry-After", "600")));

        Assert.True(lookup.RateLimited);
        Assert.InRange(lookup.LimitResetsUtc!.Value, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
    }

    [Fact]
    public async Task A_403_with_calls_left_is_not_the_rate_limit()
    {
        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None,
            new Limited(System.Net.HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", "12")));

        Assert.False(lookup.RateLimited);
        Assert.Null(lookup.Asset);
    }

    [Fact]
    public async Task A_missing_release_is_not_the_rate_limit()
    {
        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, new Limited(System.Net.HttpStatusCode.NotFound));

        Assert.False(lookup.RateLimited);
    }

    [Fact]
    public void The_message_names_the_limit_and_when_it_lifts_in_plain_words()
    {
        var text = GithubLatestRelease.RateLimitMessage("NZBGet", new DateTimeOffset(2026, 10, 8, 21, 21, 0, TimeSpan.Zero));

        Assert.Contains("hourly limit", text);
        Assert.Contains("NZBGet", text);
        Assert.Contains("Try again", text);
        Assert.DoesNotContain("Could not find", text);
        Assert.Contains("hourly limit", GithubLatestRelease.RateLimitMessage("Weir", null));
    }
}

public sealed class PendingInstallRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-pending-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_app_stopped_by_the_limit_is_kept_with_when_to_try_again_and_comes_off_once_installed()
    {
        var retry = DateTimeOffset.UtcNow.AddMinutes(40);
        PendingInstallRecorder.Record(
        [
            new ComponentInstallResult("qbittorrent", ComponentInstallOutcome.Installed, "ok"),
            new ComponentInstallResult("nzbget", ComponentInstallOutcome.DownloadFailed, "limit", retry),
            new ComponentInstallResult("weir", ComponentInstallOutcome.DownloadFailed, "limit", retry)
        ], _root);

        var pending = Deluno.Contracts.PendingComponentInstalls.Read(_root)!;
        Assert.Equal(["nzbget", "weir"], pending.Kinds);
        Assert.Equal(retry.ToUnixTimeSeconds(), pending.RetryAfterUtc!.Value.ToUnixTimeSeconds());
        Assert.False(PendingInstallRecorder.ReadyToOffer(pending, DateTimeOffset.UtcNow));
        Assert.True(PendingInstallRecorder.ReadyToOffer(pending, retry.AddSeconds(1)));
        Assert.Contains("NZBGet, Weir", PendingInstallRecorder.MenuText(pending, DateTimeOffset.UtcNow, kind => kind == "nzbget" ? "NZBGet" : "Weir"));

        PendingInstallRecorder.Record([new ComponentInstallResult("nzbget", ComponentInstallOutcome.Installed, "ok")], _root);
        Assert.Equal(["weir"], Deluno.Contracts.PendingComponentInstalls.Read(_root)!.Kinds);

        PendingInstallRecorder.Record([new ComponentInstallResult("weir", ComponentInstallOutcome.Installed, "ok")], _root);
        Assert.Null(Deluno.Contracts.PendingComponentInstalls.Read(_root));
    }

    [Fact]
    public void A_failure_that_is_not_the_limit_is_not_kept()
    {
        PendingInstallRecorder.Record([new ComponentInstallResult("nzbget", ComponentInstallOutcome.DownloadFailed, "No source")], _root);

        Assert.Null(Deluno.Contracts.PendingComponentInstalls.Read(_root));
    }

    [Fact]
    public void A_persons_ask_is_acted_on_once()
    {
        Deluno.Contracts.PendingComponentInstalls.Add(_root, ["nzbget"], null, askedByPerson: true, reason: null);
        Assert.True(Deluno.Contracts.PendingComponentInstalls.Read(_root)!.AskedByPerson);

        PendingInstallRecorder.Record([], _root);

        var after = Deluno.Contracts.PendingComponentInstalls.Read(_root)!;
        Assert.False(after.AskedByPerson);
        Assert.Equal(["nzbget"], after.Kinds);
    }
}
