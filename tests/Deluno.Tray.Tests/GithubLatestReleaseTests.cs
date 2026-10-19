using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

public sealed class GithubLatestReleaseTests
{
    // Every GitHub reply here is GitHub's own, recorded on 14 Oct 2026 (tests/fixtures/recorded/github). Weir's releases list is
    // rc.17 (published with no files), rc.13 and rc.12; where a test needs another state it changes only the value it is about.
    private const string Rc17 = "v1.0.0-rc.17";
    private const string Rc13 = "v1.0.0-rc.13";
    private const string Rc13InstallerSha = "444a5d166c92dde2730cc37977830358d77b26869b7ba86a0c3d5278d36189b7";

    private static Recording WeirRecording() => RecordedReplies.Github("weir-newest-release-has-no-files");

    private static string Releases() => WeirRecording().Find("api.github.com").Reply.Body!;

    private static string Sums(string tag) => WeirRecording().Find($"{tag}/SHA256SUMS.txt").Reply.Body!;

    private static string NzbgetSignature() => RecordedReplies.Github("nzbget-latest-release").Find("v26.3/nzbget-26.3.sig.txt").Reply.Body!;

    [Fact]
    public void Picks_the_highest_version_release_including_pre_releases_and_never_a_draft()
    {
        // rc.17 is the highest version and every Weir release is a pre-release: the listing order does not decide it.
        Assert.Equal(Rc17, GithubLatestRelease.PickRelease(Releases())!.TagName);
        var reversed = new System.Text.Json.Nodes.JsonArray(((System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(Releases())!).Reverse().Select(n => n!.DeepClone()).ToArray());
        Assert.Equal(Rc17, GithubLatestRelease.PickRelease(reversed.ToJsonString())!.TagName);

        // With rc.17 a draft (the one value changed), the next is rc.13 and its files.
        var release = GithubLatestRelease.PickRelease(WeirRecording().Body("api.github.com", ("0.draft", true)))!;

        Assert.Equal(Rc13, release.TagName);
        Assert.Equal($"https://github.com/jampat000/Weir/releases/download/{Rc13}/Weir-win-Setup.exe", release.Assets!.First(a => a.Name == "Weir-win-Setup.exe").BrowserDownloadUrl);
        Assert.Contains(release.Assets!, a => a.Name == "SHA256SUMS.txt");
    }

    [Fact]
    public void Resolves_a_repository_whose_only_release_is_a_pre_release()
    {
        var list = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(Releases())!;
        var only = new System.Text.Json.Nodes.JsonArray(list[1]!.DeepClone());

        var release = GithubLatestRelease.PickRelease(only.ToJsonString());

        Assert.Equal(Rc13, release!.TagName);
        Assert.Equal($"https://github.com/jampat000/Weir/releases/download/{Rc13}/Weir-win-Setup.exe", release.Assets!.First(a => a.Name == "Weir-win-Setup.exe").BrowserDownloadUrl);
    }

    [Fact]
    public void Orders_a_later_release_candidate_over_an_earlier_one_and_a_stable_release_over_every_candidate()
    {
        // rc.13 over rc.12 (listed in the other order), then rc.12 made the stable release v1.0.0 (its tag and pre-release flag changed): stable wins.
        var list = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(Releases())!;
        var rc13AndRc12 = new System.Text.Json.Nodes.JsonArray(list[2]!.DeepClone(), list[1]!.DeepClone());
        Assert.Equal(Rc13, GithubLatestRelease.PickRelease(rc13AndRc12.ToJsonString())!.TagName);

        Assert.Equal("v1.0.0", GithubLatestRelease.PickRelease(WeirRecording().Body("api.github.com", ("2.tag_name", "v1.0.0"), ("2.prerelease", false)))!.TagName);

        // Every release a draft: nothing to pick.
        Assert.Null(GithubLatestRelease.PickRelease(WeirRecording().Body("api.github.com", ("0.draft", true), ("1.draft", true), ("2.draft", true))));
    }

    [Fact]
    public void The_checksum_check_still_applies_to_the_pre_release_asset()
    {
        var release = GithubLatestRelease.PickRelease(WeirRecording().Body("api.github.com", ("0.draft", true)))!;

        Assert.Equal(Rc13InstallerSha, GithubLatestRelease.ParseChecksum(Sums(Rc13), release.Assets!.First(a => a.Name == "Weir-win-Setup.exe").Name!));
        Assert.Null(GithubLatestRelease.ParseChecksum(Sums(Rc13).Replace("Weir-win-Setup.exe", "OldWeir-win-Setup.exe", StringComparison.Ordinal), "Weir-win-Setup.exe"));
    }

    [Fact]
    public void Reads_the_matching_line_from_a_standard_SHA256SUMS_file()
        => Assert.Equal(Rc13InstallerSha, GithubLatestRelease.ParseChecksum(Sums(Rc13), "Weir-win-Setup.exe"));

    [Fact]
    public void Answers_null_when_the_file_has_no_matching_line()
        => Assert.Null(GithubLatestRelease.ParseChecksum(Sums(Rc13), "Weir-win-Setup-that-is-not-there.exe"));

    [Fact]
    public void Does_not_match_a_line_whose_name_merely_ends_with_the_target_file_name()
        // "Weir-win-Setup.exe" ends with "win-Setup.exe" but is not it: a pure EndsWith match would hand back its hash for a lookup of another asset.
        => Assert.Null(GithubLatestRelease.ParseChecksum(Sums(Rc13), "win-Setup.exe"));

    [Fact]
    public void Matches_a_path_prefixed_entry_by_its_base_name()
        // The recorded file with the installer's line carrying a folder (the one value changed).
        => Assert.Equal(Rc13InstallerSha, GithubLatestRelease.ParseChecksum(Sums(Rc13).Replace("  Weir-win-Setup.exe", "  dist/Weir-win-Setup.exe", StringComparison.Ordinal), "Weir-win-Setup.exe"));

    [Fact]
    public void Reads_a_hash_out_of_nzbgets_own_jsonp_signature_file()
    {
        // NZBGet's nzbget-<version>.sig.txt is not a SHA256SUMS.txt line -- it is a JSONP body naming every asset's SHA256 as its own key.
        var hash = GithubLatestRelease.ParseChecksum(NzbgetSignature(), "nzbget-26.3-bin-windows-setup.exe");

        Assert.Equal("5ddba34ef3dcc7dc3e62480a95ccf36cb02d4f0876dd2dc81ccc42cab27a3bd7", hash);
    }

    [Fact]
    public void Jsonp_signature_lookup_answers_null_for_a_different_asset_name()
        => Assert.Null(GithubLatestRelease.ParseChecksum(NzbgetSignature(), "nzbget-26.2-bin-windows-setup.exe"));
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
    // GitHub's replies are the recorded ones: NZBGet's latest release (v26.3), its releases list (a newer "testing" pre-release ahead
    // of the stable releases), its release feed and signature files; Weir's feed, list and checksum files.
    internal sealed class Replay
    {
        private readonly Recording[] _recordings;
        private readonly Func<HttpRequestMessage, HttpResponseMessage?>? _own;

        public Replay(Func<HttpRequestMessage, HttpResponseMessage?>? own, params Recording[] recordings)
        {
            _own = own;
            _recordings = recordings;
        }

        public List<string> Requests { get; } = [];

        public HttpMessageHandler Handler => RecordedReplies.Handler("github", _recordings, (request, _) =>
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return _own?.Invoke(request);
        });

        public IEnumerable<string> ApiRequests => Requests.Where(r => new Uri(r).Host == "api.github.com");
    }

    private static Replay NzbgetReplay(Func<HttpRequestMessage, HttpResponseMessage?>? own = null)
        => new(own, RecordedReplies.Github("nzbget-latest-release"), RecordedReplies.Github("nzbget-release-feed"));

    private static Replay WeirReplay(Func<HttpRequestMessage, HttpResponseMessage?>? own = null)
        => new(own, RecordedReplies.Github("weir-newest-release-has-no-files"));

    /// <summary>The 503 of a release feed that cannot be read (a status with no body, which GitHub gives and a recording cannot hold), so the lookup takes the API route.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> FeedDown(Func<HttpRequestMessage, HttpResponseMessage?>? then = null)
        => request => request.RequestUri!.AbsoluteUri.EndsWith("/releases.atom", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            : then?.Invoke(request);

    private const string Nzbget263Installer = "https://github.com/nzbgetcom/nzbget/releases/download/v26.3/nzbget-26.3-bin-windows-setup.exe";

    [Fact]
    public async Task A_component_without_the_flag_takes_the_latest_stable_release_even_when_a_newer_pre_release_exists()
    {
        var nzbget = BundledComponents.Load().Single(s => s.Kind == "nzbget");
        Assert.False(nzbget.IncludePreReleases);
        // NZBGet's installer name carries its version, so it is found through the API's latest release; the API's list holds a newer "testing" pre-release that must not be taken.
        var github = NzbgetReplay();

        var resolved = await GithubLatestRelease.ResolveAsync(nzbget, CancellationToken.None, github.Handler);

        Assert.Equal(Nzbget263Installer, resolved!.AssetUrl);
        Assert.Equal("5ddba34ef3dcc7dc3e62480a95ccf36cb02d4f0876dd2dc81ccc42cab27a3bd7", resolved.Sha256);
        // One API call, for the latest release; the other call is the release's own signature file.
        var api = Assert.Single(github.ApiRequests);
        Assert.EndsWith("/releases/latest", api);
    }

    [Fact]
    public async Task Weir_takes_the_newest_release_including_a_pre_release()
    {
        var weir = BundledComponents.Load().Single(s => s.Kind == "weir");
        Assert.True(weir.IncludePreReleases);
        // Through the API: Weir's list is release candidates only, with rc.12 changed into an older stable release (its tag and flag).
        var recording = RecordedReplies.Github("weir-newest-release-has-no-files");
        var github = WeirReplay(FeedDown(request => request.RequestUri!.AbsoluteUri.Contains("/releases?per_page=100", StringComparison.Ordinal)
            ? RecordedReplies.Json(recording.Body("api.github.com", ("2.tag_name", "v0.9.0"), ("2.prerelease", false)))
            : null));

        var resolved = await GithubLatestRelease.ResolveAsync(weir, CancellationToken.None, github.Handler);

        Assert.Equal("https://github.com/jampat000/Weir/releases/download/v1.0.0-rc.13/Weir-win-Setup.exe", resolved!.AssetUrl);
    }

    // #1181: the free route first. The newest tag comes from github.com's release feed and the files from the release
    // download folder; neither counts against GitHub's 60 API calls an hour.
    [Fact]
    public async Task Weir_is_found_through_the_release_feed_release_candidates_included_without_the_api()
    {
        var weir = BundledComponents.Load().Single(s => s.Kind == "weir");
        var handler = WeirReplay(request => request.RequestUri!.Host == "api.github.com" ? throw new InvalidOperationException("the API must not be called") : null);

        var resolved = await GithubLatestRelease.ResolveAsync(weir, CancellationToken.None, handler.Handler);

        // rc.17 is the newest tag but has no files: the newest release that has them is used.
        Assert.Equal("https://github.com/EJM-Digital/Weir/releases/download/v1.0.0-rc.13/Weir-win-Setup.exe", resolved!.AssetUrl);
        Assert.Equal("444a5d166c92dde2730cc37977830358d77b26869b7ba86a0c3d5278d36189b7", resolved.Sha256);
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
    public async Task A_404_from_the_release_feed_says_the_feed_is_unreachable_and_does_not_fall_back_to_the_api()
    {
        var seen = new List<string>();
        var weir = BundledComponents.Load().Single(s => s.Kind == "weir") with { GithubRepo = "jampat000/no-such-repo-1263" };
        var replay = new DirectoryListingLatestReleaseTests.Replay(request => { seen.Add(request.RequestUri!.AbsoluteUri); return null; }, RecordedReplies.Github("release-feed-not-found"));

        var lookup = await GithubLatestRelease.LookupAsync(weir, CancellationToken.None, replay.Handler);

        Assert.True(lookup.FeedNotFound);
        Assert.False(lookup.RateLimited);
        Assert.Null(lookup.Asset);
        Assert.DoesNotContain(seen, url => url.Contains("api.github.com"));
        Assert.Contains("release feed isn't reachable", GithubLatestRelease.FeedNotFoundMessage("Weir"));
    }

    [Fact]
    public void The_message_is_the_servers_sentence_with_the_reset_in_this_computers_time()
    {
        var at = new DateTimeOffset(2026, 10, 8, 21, 21, 0, TimeSpan.Zero);

        Assert.Equal(Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor(at), GithubLatestRelease.RateLimitMessage(at, TimeZoneInfo.Utc).Replace("9:21 pm", "2026-10-08 21:21 UTC"));
        Assert.Contains("9:21 pm", GithubLatestRelease.RateLimitMessage(at, TimeZoneInfo.Utc));
        Assert.DoesNotContain("UTC", GithubLatestRelease.RateLimitMessage(at, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task The_api_fallback_reuses_a_304_body_and_remembers_the_limit_with_no_call_before_the_reset()
    {
        var state = new Deluno.Infrastructure.Http.GitHubApiState();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var reset = clock.GetUtcNow().AddMinutes(30);
        var api = new ApiSequence(reset);
        var nzbget = Nzbget();

        var first = await GithubLatestRelease.LookupAsync(nzbget, CancellationToken.None, api, state, clock);
        var second = await GithubLatestRelease.LookupAsync(nzbget, CancellationToken.None, api, state, clock);

        Assert.NotNull(first.Asset);
        Assert.Equal(first.Asset!.AssetUrl, second.Asset!.AssetUrl);
        Assert.Equal("\"v1\"", Assert.Single(api.IfNoneMatchSeen, v => v is not null));

        var third = await GithubLatestRelease.LookupAsync(nzbget, CancellationToken.None, api, state, clock);
        Assert.True(third.RateLimited);
        Assert.Equal(reset, third.LimitResetsUtc);
        var calls = api.Calls;

        var fourth = await GithubLatestRelease.LookupAsync(nzbget, CancellationToken.None, api, state, clock);
        Assert.True(fourth.RateLimited);
        Assert.Equal(calls, api.Calls);

        clock.Now = reset.AddSeconds(1);
        api.StopLimiting();
        Assert.False((await GithubLatestRelease.LookupAsync(nzbget, CancellationToken.None, api, state, clock)).RateLimited);
        Assert.True(api.Calls > calls);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// api.github.com: call 1 answers with an ETag, call 2 answers 304, call 3 is the limit. The release is GitHub's own recorded
    /// reply for NZBGet's latest release, and its signature file is the recorded download (github.com, not counted as an API call).
    /// </summary>
    private sealed class ApiSequence(DateTimeOffset reset) : HttpMessageHandler
    {
        private readonly Deluno.Tests.Recorded.Recording _recording = Deluno.Tests.Recorded.RecordedReplies.Github("nzbget-latest-release");
        private bool limiting = true;

        public int Calls { get; private set; }

        public List<string?> IfNoneMatchSeen { get; } = [];

        public void StopLimiting() => limiting = false;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                return Task.FromResult(_recording.Find(Path.GetFileName(request.RequestUri.AbsolutePath)).Reply.ToResponse());
            }

            Calls++;
            IfNoneMatchSeen.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            if (Calls == 1 || !limiting)
            {
                var ok = _recording.Find("releases/latest").Reply.ToResponse();
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(ok);
            }

            if (Calls == 2)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotModified));
            }

            var limited = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return Task.FromResult(limited);
        }
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
