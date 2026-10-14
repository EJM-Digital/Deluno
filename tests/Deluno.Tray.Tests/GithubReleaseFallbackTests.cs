using System.Text.Json.Nodes;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1263: on 14 Oct 2026 Weir published v1.0.0-rc.17 on GitHub as a pre-release before its files were uploaded
/// (assets []), and the component picker gave up with "Could not find Weir's latest release on GitHub" although
/// v1.0.0-rc.13 had every file. The picker uses the newest release that has the installer, its checksum file and a
/// checksum line for the installer, and says so in the install log. Every reply here is GitHub's own, recorded on
/// that day (tests/fixtures/recorded/github). Where a test needs a situation GitHub was not in at the time (the newest
/// release with an installer but no checksum file), it changes only that value in the recorded reply and says so.
/// </summary>
public sealed class GithubReleaseFallbackTests
{
    private const string Rc17 = "v1.0.0-rc.17";
    private const string Rc13 = "v1.0.0-rc.13";
    private const string Rc12 = "v1.0.0-rc.12";
    private const string Rc13InstallerSha = "444a5d166c92dde2730cc37977830358d77b26869b7ba86a0c3d5278d36189b7";
    private const string Rc12InstallerSha = "81d40beddd5db7cdbb5dcf1ed8d13cfddfcd52bdb711f53f99ea0f20a42c75a5";

    private static BundledComponentSpec Weir() => BundledComponents.Load().Single(s => s.Kind == "weir");

    private static BundledComponentSpec Nzbget() => BundledComponents.Load().Single(s => s.Kind == "nzbget");

    private static Recording WeirRecording() => RecordedReplies.Github("weir-newest-release-has-no-files");

    private static Recording NzbgetRecording() => RecordedReplies.Github("nzbget-latest-release");

    /// <summary>Answers from the recording and notes every request, so a test can count them. <paramref name="own"/> answers a call itself.</summary>
    private sealed class Replay(Recording recording, Func<HttpRequestMessage, HttpResponseMessage?>? own = null)
    {
        public List<string> Requests { get; } = [];

        public int ApiCalls => Requests.Count(r => r.Contains("://api.github.com/", StringComparison.Ordinal));

        public HttpMessageHandler Handler => RecordedReplies.Handler("github", [recording], (request, _) =>
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri!.AbsoluteUri);
            return own?.Invoke(request);
        });
    }

    private static HttpResponseMessage Unavailable() => new(System.Net.HttpStatusCode.ServiceUnavailable);

    /// <summary>The recorded 404 GitHub gave for a release whose files were not uploaded (rc.17's checksum file).</summary>
    private static HttpResponseMessage NoFile() => WeirRecording().Find($"{Rc17}/SHA256SUMS.txt").Reply.ToResponse();

    private static bool Is(HttpRequestMessage request, string part) => request.RequestUri!.AbsoluteUri.Contains(part, StringComparison.Ordinal);

    private static JsonArray ApiReleases() => (JsonArray)JsonNode.Parse(WeirRecording().Find("api.github.com").Reply.Body!)!;

    private static JsonNode Release(JsonArray releases, string tag) => releases.Single(r => r!["tag_name"]!.GetValue<string>() == tag)!;

    private static HttpResponseMessage Json(JsonNode node) => RecordedReplies.Json(node.ToJsonString());

    private static HttpResponseMessage Text(string text) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>The recorded rc.13 checksum file with the installer's own line taken out (the value this test is about).</summary>
    private static string Rc13ChecksumsWithoutTheInstaller()
        => string.Join('\n', WeirRecording().Find($"{Rc13}/SHA256SUMS.txt").Reply.Body!.Split('\n')
            .Where(line => !line.Contains("Weir-win-Setup.exe", StringComparison.Ordinal)));

    // ---- The release feed route (no API call) ----

    [Fact]
    public async Task The_newest_release_has_no_files_so_the_newest_one_that_has_them_is_used_and_verified_against_its_own_checksum()
    {
        var replay = new Replay(WeirRecording());

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal($"https://github.com/jampat000/Weir/releases/download/{Rc13}/Weir-win-Setup.exe", lookup.Asset?.AssetUrl);
        Assert.Equal(Rc13InstallerSha, lookup.Asset?.Sha256);
        Assert.Equal(Rc13, lookup.Tag);
        Assert.Contains(lookup.Skipped ?? [], skipped => skipped.StartsWith(Rc17, StringComparison.Ordinal));
        Assert.False(lookup.NoInstallerYet);
        Assert.Equal(0, replay.ApiCalls);
        // One feed, then one checksum file per release looked at.
        Assert.Equal(3, replay.Requests.Count);
    }

    [Fact]
    public async Task The_install_log_names_the_release_used_and_why_the_newer_one_was_skipped()
    {
        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, new Replay(WeirRecording()).Handler);

        var line = lookup.ChoiceLogLine("Weir");

        Assert.NotNull(line);
        Assert.Contains(Rc13, line);
        Assert.Contains(Rc17, line);
        Assert.Contains("no SHA256SUMS.txt", line);
    }

    [Fact]
    public async Task When_the_newest_release_is_the_one_used_the_log_has_nothing_to_explain()
    {
        // rc.17's checksum file answers as rc.13's does, so the newest release is whole.
        var recording = WeirRecording();
        var replay = new Replay(recording, request => Is(request, $"{Rc17}/SHA256SUMS.txt")
            ? recording.Find($"{Rc13}/SHA256SUMS.txt").Reply.ToResponse()
            : null);

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal(Rc17, lookup.Tag);
        Assert.Empty(lookup.Skipped ?? []);
        Assert.Null(lookup.ChoiceLogLine("Weir"));
    }

    [Fact]
    public async Task A_checksum_file_that_does_not_list_the_installer_skips_that_release()
    {
        // rc.17 has no files (recorded) and rc.13's checksum file lacks the installer's line (the one value changed).
        var replay = new Replay(WeirRecording(), request => Is(request, $"{Rc13}/SHA256SUMS.txt") ? Text(Rc13ChecksumsWithoutTheInstaller()) : null);

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal(Rc12, lookup.Tag);
        Assert.Equal(Rc12InstallerSha, lookup.Asset?.Sha256);
        Assert.Contains(lookup.Skipped!, skipped => skipped.StartsWith(Rc13, StringComparison.Ordinal) && skipped.Contains("does not list", StringComparison.Ordinal));
        Assert.Equal(4, replay.Requests.Count);
    }

    [Fact]
    public async Task Every_release_without_files_gives_one_plain_message_and_no_more_than_six_calls()
    {
        var replay = new Replay(WeirRecording(), request => Is(request, "/SHA256SUMS.txt") ? NoFile() : null);

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Null(lookup.Asset);
        Assert.True(lookup.NoInstallerYet);
        Assert.False(lookup.RateLimited);
        Assert.False(lookup.FeedNotFound);
        Assert.Equal("Weir's newest releases have no Windows installer yet; try again in a few minutes.", GithubLatestRelease.NoInstallerYetMessage("Weir"));
        // The feed plus the five newest releases: the budget the picker states.
        Assert.Equal(6, replay.Requests.Count);
        Assert.Equal(0, replay.ApiCalls);
    }

    [Fact]
    public async Task A_feed_that_is_not_found_says_so_and_looks_at_no_release()
    {
        var recording = RecordedReplies.Github("release-feed-not-found");
        var replay = new Replay(recording);

        var lookup = await GithubLatestRelease.LookupAsync(Weir() with { GithubRepo = "jampat000/no-such-repo-1263" }, CancellationToken.None, replay.Handler);

        Assert.True(lookup.FeedNotFound);
        Assert.Null(lookup.Asset);
        Assert.False(lookup.NoInstallerYet);
        Assert.Single(replay.Requests);
    }

    // ---- The API route (when the feed cannot be read) ----

    private static Func<HttpRequestMessage, HttpResponseMessage?> FeedDown(Func<HttpRequestMessage, HttpResponseMessage?>? then = null)
        => request => Is(request, "/releases.atom") ? Unavailable() : then?.Invoke(request);

    [Fact]
    public async Task Through_the_api_a_newest_release_with_no_assets_is_skipped_for_the_newest_that_has_them()
    {
        var replay = new Replay(WeirRecording(), FeedDown());

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal(Rc13, lookup.Tag);
        Assert.Equal(Rc13InstallerSha, lookup.Asset?.Sha256);
        Assert.Contains(lookup.Skipped!, skipped => skipped.StartsWith(Rc17, StringComparison.Ordinal) && skipped.Contains("no Weir-win-Setup.exe", StringComparison.Ordinal));
        // The feed, the list of releases, and the one checksum file of the release used: the assets of every release came in the list.
        Assert.Equal(1, replay.ApiCalls);
        Assert.Equal(3, replay.Requests.Count);
    }

    [Fact]
    public async Task Through_the_api_a_newest_release_with_the_installer_but_no_checksum_file_is_skipped()
    {
        // rc.17 is given rc.13's installer asset and nothing else (the one value changed).
        var releases = ApiReleases();
        var installer = ((JsonArray)Release(releases, Rc13)["assets"]!).Single(a => a!["name"]!.GetValue<string>() == "Weir-win-Setup.exe")!.DeepClone();
        installer["browser_download_url"] = installer["browser_download_url"]!.GetValue<string>().Replace(Rc13, Rc17, StringComparison.Ordinal);
        Release(releases, Rc17)["assets"] = new JsonArray(installer);
        var replay = new Replay(WeirRecording(), FeedDown(request => Is(request, "/releases?per_page=100") ? Json(releases) : null));

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal(Rc13, lookup.Tag);
        Assert.Contains(lookup.Skipped!, skipped => skipped.StartsWith(Rc17, StringComparison.Ordinal) && skipped.Contains("no SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Through_the_api_a_newest_checksum_file_that_does_not_list_the_installer_is_skipped()
    {
        // rc.17 is given rc.13's installer and checksum assets (retargeted at rc.17), and its checksum file lacks the installer's line.
        var releases = ApiReleases();
        JsonNode Asset(string name)
        {
            var asset = ((JsonArray)Release(releases, Rc13)["assets"]!).Single(a => a!["name"]!.GetValue<string>() == name)!.DeepClone();
            asset["browser_download_url"] = asset["browser_download_url"]!.GetValue<string>().Replace(Rc13, Rc17, StringComparison.Ordinal);
            return asset;
        }

        Release(releases, Rc17)["assets"] = new JsonArray(Asset("Weir-win-Setup.exe"), Asset("SHA256SUMS.txt"));
        var replay = new Replay(WeirRecording(), FeedDown(request =>
            Is(request, "/releases?per_page=100") ? Json(releases)
            : Is(request, $"{Rc17}/SHA256SUMS.txt") ? Text(Rc13ChecksumsWithoutTheInstaller())
            : null));

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Equal(Rc13, lookup.Tag);
        Assert.Contains(lookup.Skipped!, skipped => skipped.StartsWith(Rc17, StringComparison.Ordinal) && skipped.Contains("does not list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Through_the_api_every_release_lacking_assets_gives_the_one_plain_message()
    {
        var releases = ApiReleases();
        foreach (var release in releases)
        {
            release!["assets"] = new JsonArray();
        }

        var replay = new Replay(WeirRecording(), FeedDown(request => Is(request, "/releases?per_page=100") ? Json(releases) : null));

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.Null(lookup.Asset);
        Assert.True(lookup.NoInstallerYet);
        Assert.Equal(1, replay.ApiCalls);
        Assert.Equal(2, replay.Requests.Count);
    }

    [Fact]
    public async Task Through_the_api_the_rate_limit_keeps_its_message_and_is_not_a_missing_installer()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(25).ToUnixTimeSeconds();
        var replay = new Replay(WeirRecording(), FeedDown(request =>
        {
            if (!Is(request, "/releases?per_page=100")) return null;
            var limited = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString());
            return limited;
        }));

        var lookup = await GithubLatestRelease.LookupAsync(Weir(), CancellationToken.None, replay.Handler);

        Assert.True(lookup.RateLimited);
        Assert.False(lookup.NoInstallerYet);
        Assert.Null(lookup.Asset);
        Assert.Equal(reset, lookup.LimitResetsUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal(Deluno.Infrastructure.Http.GitHubRateLimitException.LocalizeResetTimes(Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor(lookup.LimitResetsUtc.Value), TimeZoneInfo.Utc),
            GithubLatestRelease.RateLimitMessage(lookup.LimitResetsUtc.Value, TimeZoneInfo.Utc));
    }

    // ---- Next door: NZBGet is found through GitHub releases too ----

    private static JsonNode NzbgetWithoutItsSignatureFile(JsonNode release)
    {
        var assets = (JsonArray)release["assets"]!;
        foreach (var sig in assets.Where(a => a!["name"]!.GetValue<string>().EndsWith(".sig.txt", StringComparison.Ordinal)).ToList())
        {
            assets.Remove(sig);
        }

        return release;
    }

    [Fact]
    public async Task NZBGets_newest_release_is_used_with_its_own_signature_file_when_it_is_whole()
    {
        var replay = new Replay(NzbgetRecording());

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.Equal("v26.3", lookup.Tag);
        Assert.EndsWith("/nzbget-26.3-bin-windows-setup.exe", lookup.Asset!.AssetUrl);
        Assert.Matches("^[0-9a-f]{64}$", lookup.Asset.Sha256);
        Assert.Empty(lookup.Skipped ?? []);
        Assert.Equal(1, replay.ApiCalls);
    }

    [Fact]
    public async Task NZBGets_newest_release_missing_its_signature_file_falls_back_to_the_next_stable_release()
    {
        // 26.3 loses its signature file in both the latest answer and the list (the one value changed); 26.2 is whole.
        var recording = NzbgetRecording();
        var latest = NzbgetWithoutItsSignatureFile(JsonNode.Parse(recording.Find("releases/latest").Reply.Body!)!);
        var list = (JsonArray)JsonNode.Parse(recording.Find("releases?per_page=100").Reply.Body!)!;
        NzbgetWithoutItsSignatureFile(list.Single(r => r!["tag_name"]!.GetValue<string>() == "v26.3")!);
        var replay = new Replay(recording, request =>
            Is(request, "/releases/latest") ? Json(latest)
            : Is(request, "/releases?per_page=100") ? Json(list)
            : null);

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.Equal("v26.2", lookup.Tag);
        Assert.EndsWith("/nzbget-26.2-bin-windows-setup.exe", lookup.Asset!.AssetUrl);
        Assert.Matches("^[0-9a-f]{64}$", lookup.Asset.Sha256);
        Assert.Contains(lookup.Skipped!, skipped => skipped.StartsWith("v26.3", StringComparison.Ordinal));
        Assert.Equal(2, replay.ApiCalls);
        // The "testing" pre-release at the top of the list is never offered to a component that does not take pre-releases.
        Assert.DoesNotContain("testing", lookup.Asset.AssetUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NZBGet_with_no_stable_release_that_has_its_files_gives_the_one_plain_message()
    {
        var recording = NzbgetRecording();
        var latest = JsonNode.Parse(recording.Find("releases/latest").Reply.Body!)!;
        latest["assets"] = new JsonArray();
        var list = (JsonArray)JsonNode.Parse(recording.Find("releases?per_page=100").Reply.Body!)!;
        foreach (var release in list)
        {
            release!["assets"] = new JsonArray();
        }

        var replay = new Replay(recording, request =>
            Is(request, "/releases/latest") ? Json(latest)
            : Is(request, "/releases?per_page=100") ? Json(list)
            : null);

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.True(lookup.NoInstallerYet);
        Assert.Equal("NZBGet's newest releases have no Windows installer yet; try again in a few minutes.", GithubLatestRelease.NoInstallerYetMessage("NZBGet"));
        Assert.Equal(2, replay.ApiCalls);
    }
}
