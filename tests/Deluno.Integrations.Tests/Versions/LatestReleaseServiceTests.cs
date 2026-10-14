using System.Net;
using System.Text;
using Deluno.Connections.Versions;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1117: the newest stable release of each project, read from recorded feed answers and cached for a day. No real network.</summary>
public sealed class LatestReleaseServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    // Every GitHub reply here is GitHub's own, recorded on 14 Oct 2026 (tests/fixtures/recorded/github). Where a test needs another
    // state it changes only the value it is about, and says so.
    private static string SabLatest() => RecordedReplies.Github("sabnzbd-latest-release").Single.Reply.Body!;

    private static string SabLatestWith(params (string Path, object? Value)[] edits) => RecordedReplies.Github("sabnzbd-latest-release").Single.Reply.BodyWith(edits);

    private static string WeirReleaseList() => RecordedReplies.Github("weir-newest-release-has-no-files").Find("api.github.com").Reply.Body!;

    private static string WeirReleaseListWith(params (string Path, object? Value)[] edits) => RecordedReplies.Github("weir-newest-release-has-no-files").Body("api.github.com", edits);

    private static string QbittorrentTags() => RecordedReplies.Github("qbittorrent-tags").Find("api.github.com").Reply.Body!;

    private static string WeirAtom() => RecordedReplies.Github("weir-newest-release-has-no-files").Find("Weir/releases.atom").Reply.Body!;

    private static string NzbgetAtom() => RecordedReplies.Github("nzbget-release-feed").Single.Reply.Body!;

    private static string QbittorrentTagsAtom() => RecordedReplies.Github("qbittorrent-tags").Find("tags.atom").Reply.Body!;

    /// <summary>The page address GitHub's own feed gives for a tag.</summary>
    private static string PageOf(string atom, string tag)
        => System.Text.RegularExpressions.Regex.Match(atom, "href=\"([^\"]+/" + System.Text.RegularExpressions.Regex.Escape(tag) + ")\"").Groups[1].Value;

    private static string HtmlUrlOf(string json, int index = -1)
        => (index < 0 ? System.Text.Json.Nodes.JsonNode.Parse(json)! : System.Text.Json.Nodes.JsonNode.Parse(json)![index]!)["html_url"]!.GetValue<string>();

    /// <summary>The recorded tags list with only the tags the filter keeps.</summary>
    private static string TagsWhere(string tagsJson, Func<string, bool> keep)
        => new System.Text.Json.Nodes.JsonArray(((System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(tagsJson)!)
            .Where(tag => keep(tag!["name"]!.GetValue<string>())).Select(tag => tag!.DeepClone()).ToArray()).ToJsonString();

    [Fact]
    public void GitHub_latest_release_gives_its_version_and_page()
    {
        var release = ReleaseFeedParsers.FromGitHubRelease(SabLatest());

        Assert.Equal("5.1.3", release!.Version);
        Assert.Equal(HtmlUrlOf(SabLatest()), release.Url);
        Assert.StartsWith("https://github.com/sabnzbd/sabnzbd/releases/", release.Url);
    }

    [Fact]
    public void A_prerelease_draft_or_unexpected_answer_is_no_release()
    {
        // The recorded latest release with one value changed each time.
        Assert.Null(ReleaseFeedParsers.FromGitHubRelease(SabLatestWith(("prerelease", true))));
        Assert.Null(ReleaseFeedParsers.FromGitHubRelease(SabLatestWith(("draft", true))));
        Assert.Null(ReleaseFeedParsers.FromGitHubRelease(SabLatestWith(("tag_name", "v5.2.0-beta1"))));
        // GitHub's own 404 for a repository with no such release, and text that is no JSON.
        Assert.Null(ReleaseFeedParsers.FromGitHubRelease(RecordedReplies.Github("api-not-found").Single.Reply.Body!));
        Assert.Null(ReleaseFeedParsers.FromGitHubRelease("not json"));
    }

    [Fact]
    public void A_release_page_that_is_not_https_is_not_linked()
        => Assert.Null(ReleaseFeedParsers.FromGitHubRelease(SabLatestWith(("html_url", "javascript:alert(1)")))!.Url);

    [Fact]
    public void Qbittorrent_tags_give_the_highest_stable_release()
    {
        // The tags page lists release-5.3.0rc1 and release-5.3.0beta1 ahead of the stable release-5.2.4.
        var release = ReleaseFeedParsers.FromGitHubTags(QbittorrentTags(), "qbittorrent/qBittorrent", "release-");

        Assert.Equal("5.2.4", release!.Version);
        Assert.Equal(PageOf(QbittorrentTagsAtom(), "release-5.2.4"), release.Url);
        Assert.StartsWith("https://github.com/qbittorrent/qBittorrent/releases/", release.Url);
    }

    [Fact]
    public void Deluge_tags_skip_development_tags_and_compare_numbers_not_text()
    {
        var tags = RecordedReplies.Github("deluge-tags").Find("api.github.com").Reply.Body!;

        // deluge-2.2.1.dev0 is listed first; the newest release is 2.2.0.
        Assert.Equal("2.2.0", ReleaseFeedParsers.FromGitHubTags(tags, "deluge-torrent/deluge", "deluge-")!.Version);
        // Among the 1.3 releases, 1.3.15 is higher than 1.3.9 although it sorts below it as text.
        Assert.Equal("1.3.15", ReleaseFeedParsers.FromGitHubTags(TagsWhere(tags, name => name.StartsWith("deluge-1.3.", StringComparison.Ordinal) && !name.Contains("rc", StringComparison.Ordinal)), "deluge-torrent/deluge", "deluge-")!.Version);
    }

    [Fact]
    public void Tags_with_nothing_stable_give_no_release()
        => Assert.Null(ReleaseFeedParsers.FromGitHubTags(TagsWhere(QbittorrentTags(), name => name.Contains("rc1", StringComparison.Ordinal) || name.Contains("beta1", StringComparison.Ordinal)), "qbittorrent/qBittorrent", "release-"));

    [Fact]
    public void Plex_downloads_give_the_public_server_version()
    {
        var release = ReleaseFeedParsers.FromPlexDownloads(
            """{"computer":{"Windows":{"id":"windows","name":"Windows","release_date":1786974203,"version":"1.43.4.10903-e5521bd8c"},"Linux":{"version":"1.43.4.10903-e5521bd8c"}}}""");

        Assert.Equal("1.43.4.10903-e5521bd8c", release!.Version);
        Assert.False(ReleaseVersions_IsOutOfDate("1.43.4.10903-b5521bd8c", release.Version));
        Assert.True(ReleaseVersions_IsOutOfDate("1.43.3.10540-aaaaaaaaa", release.Version));
    }

    [Fact]
    public void Plex_downloads_of_another_shape_give_no_release()
        => Assert.Null(ReleaseFeedParsers.FromPlexDownloads("""{"mobile":{}}"""));

    [Fact]
    public async Task A_feed_is_read_once_a_day_however_often_it_is_asked_about()
    {
        var clock = new Clock(Start);
        var handler = new CountingHandler(_ => Ok(SabLatest()));
        var service = Service(handler, clock);

        for (var i = 0; i < 5; i++)
        {
            await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        }

        clock.Now = Start.AddHours(23);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("5.1.3", service.Peek("sabnzbd")!.Version);
        Assert.Equal(1, handler.Calls);

        clock.Now = Start.AddHours(24).AddMinutes(1);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Each_project_is_read_separately()
    {
        var handler = new CountingHandler(_ => Ok(SabLatest()));
        var service = Service(handler, new Clock(Start));

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        await service.RefreshIfDueAsync("nzbget", CancellationToken.None);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Peek_never_waits_and_starts_the_read_in_the_background()
    {
        var release = new TaskCompletionSource();
        var handler = new CountingHandler(async _ =>
        {
            await release.Task;
            return Ok(SabLatest());
        });
        var service = Service(handler, new Clock(Start));

        var first = service.Peek("sabnzbd");
        Assert.Null(first);

        release.SetResult();
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Equal("5.1.3", service.Peek("sabnzbd")!.Version);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_failed_read_is_no_answer_not_an_error_and_is_tried_again_in_an_hour()
    {
        var clock = new Clock(Start);
        var fail = true;
        var handler = new CountingHandler(_ => fail ? throw new HttpRequestException("offline") : Ok(SabLatest()));
        var service = Service(handler, clock);

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Null(service.Peek("sabnzbd"));

        clock.Now = Start.AddMinutes(30);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        fail = false;
        clock.Now = Start.AddMinutes(61);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal("5.1.3", service.Peek("sabnzbd")!.Version);
    }

    [Fact]
    public async Task A_rate_limited_answer_keeps_the_last_good_release()
    {
        var clock = new Clock(Start);
        var status = HttpStatusCode.OK;
        var handler = new CountingHandler(_ => status == HttpStatusCode.OK
            ? Ok(SabLatest())
            : new HttpResponseMessage(status) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") });
        var service = Service(handler, clock);

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        status = HttpStatusCode.Forbidden;
        clock.Now = Start.AddHours(25);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Equal("5.1.3", service.Peek("sabnzbd")!.Version);
    }

    [Fact]
    public void A_project_with_no_feed_is_null_and_asks_nothing()
    {
        var handler = new CountingHandler(_ => Ok("{}"));
        var service = Service(handler, new Clock(Start));

        Assert.Null(service.Peek("utorrent"));
        Assert.Null(service.Peek(""));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void A_feed_that_includes_pre_releases_takes_the_highest_version_not_the_newest_date_and_skips_drafts()
    {
        var release = ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseList());

        Assert.Equal("1.0.0-rc.17", release!.Version);
        Assert.Equal(HtmlUrlOf(WeirReleaseList(), 0), release.Url);

        // With rc.17 a draft (the one value changed), the next is rc.13.
        Assert.Equal("1.0.0-rc.13", ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseListWith(("0.draft", true)))!.Version);
    }

    [Fact]
    public void A_repository_with_only_a_pre_release_resolves_to_it()
        => Assert.Equal("1.0.0-rc.17", ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseListWith(("^", 1)))!.Version);

    [Fact]
    public void A_stable_release_beats_every_rc_of_the_same_numbers()
        // rc.12 made the stable v1.0.0 (its tag and pre-release flag changed), listed below rc.17 and rc.13.
        => Assert.Equal("1.0.0", ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseListWith(("2.tag_name", "v1.0.0"), ("2.prerelease", false)))!.Version);

    [Fact]
    public void No_usable_release_in_the_list_is_no_release()
    {
        Assert.Null(ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseListWith(("0.draft", true), ("1.draft", true), ("2.draft", true))));
        Assert.Null(ReleaseFeedParsers.FromGitHubReleaseList("[]"));
        Assert.Null(ReleaseFeedParsers.FromGitHubReleaseList(RecordedReplies.Github("api-not-found").Single.Reply.Body!));
        Assert.Null(ReleaseFeedParsers.FromGitHubReleaseList("not json"));
    }

    [Fact]
    public void Only_the_feed_that_asks_for_pre_releases_lists_them_and_the_others_stay_on_latest()
    {
        var weir = new GitHubReleasesFeed("weir", "jampat000/Weir", IncludePreReleases: true);
        var sab = new GitHubReleasesFeed("sabnzbd", "sabnzbd/sabnzbd");

        Assert.Contains("/releases?", weir.RequestUri.AbsoluteUri);
        Assert.EndsWith("/releases/latest", sab.RequestUri.AbsoluteUri);
        Assert.Equal("1.0.0-rc.17", weir.Pick(WeirReleaseList())!.Version);
        // The recorded latest release with one value changed each time.
        Assert.Null(sab.Pick(SabLatestWith(("tag_name", "v5.2.0-rc1"))));
        Assert.Null(sab.Pick(SabLatestWith(("prerelease", true))));
    }

    [Fact]
    public void A_weir_on_an_earlier_candidate_hears_of_the_newest_and_of_the_stable_release_after_it()
    {
        var newest = ReleaseFeedParsers.FromGitHubReleaseList(WeirReleaseList())!.Version;

        Assert.True(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.12", newest));
        Assert.True(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.17", "1.0.0"));
        Assert.False(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.17", newest));
    }

    // ---- #1181: the free route first, the limit remembered

    [Fact]
    public void The_free_route_is_the_releases_page_on_github_com_and_takes_release_candidates_only_where_asked()
    {
        var weir = new GitHubReleasesFeed("weir", "jampat000/Weir", IncludePreReleases: true);
        var weirStableOnly = new GitHubReleasesFeed("weir", "jampat000/Weir");
        var nzbget = new GitHubReleasesFeed("nzbget", "nzbgetcom/nzbget");

        Assert.Equal("https://github.com/jampat000/Weir/releases.atom", weir.FreeRequestUri!.AbsoluteUri);
        Assert.Equal("1.0.0-rc.17", weir.PickFree(WeirAtom())!.Version);
        Assert.Equal(PageOf(WeirAtom(), "v1.0.0-rc.17"), weir.PickFree(WeirAtom())!.Url);
        // Weir has published release candidates only, so a feed that wants stable releases finds none.
        Assert.Null(weirStableOnly.PickFree(WeirAtom()));
        // NZBGet's feed leads with a rolling "testing" tag, which is no version.
        Assert.Equal("26.3", nzbget.PickFree(NzbgetAtom())!.Version);
        Assert.Null(nzbget.PickFree(string.Empty));
    }

    [Fact]
    public void A_tag_only_project_reads_its_tags_page_by_prefix()
    {
        var feed = new GitHubTagsFeed("qbittorrent", "qbittorrent/qBittorrent", "release-");

        Assert.Equal("https://github.com/qbittorrent/qBittorrent/tags.atom", feed.FreeRequestUri!.AbsoluteUri);
        Assert.Equal("5.2.4", feed.PickFree(QbittorrentTagsAtom())!.Version);
    }

    /// <summary>GitHub's recorded replies, noting which host each call went to. <paramref name="own"/> may answer a call first.</summary>
    private static HttpMessageHandler Github(List<string> seen, Func<HttpRequestMessage, HttpResponseMessage?>? own = null, params string[] recordings)
        => RecordedReplies.Handler("github", recordings.Select(RecordedReplies.Github), (request, _) =>
        {
            seen.Add(request.RequestUri!.Host);
            return own?.Invoke(request);
        });

    [Fact]
    public async Task The_newest_release_is_read_from_the_free_page_and_the_api_is_never_called()
    {
        var seen = new List<string>();
        var handler = Github(seen, request => request.RequestUri!.Host == "api.github.com" ? throw new InvalidOperationException("the API must not be called") : null, "weir-newest-release-has-no-files");
        var service = new LatestReleaseService(new Factory(handler), [new GitHubReleasesFeed("weir", "jampat000/Weir", IncludePreReleases: true)], new Clock(Start));

        await service.RefreshIfDueAsync("weir", CancellationToken.None);

        Assert.Equal("1.0.0-rc.17", service.Peek("weir")!.Version);
        Assert.Equal(["github.com"], seen);
    }

    [Fact]
    public async Task The_api_is_asked_only_when_the_free_page_cannot_be_read()
    {
        var seen = new List<string>();
        // The free page cannot be read (a 503, a status with no body); the API answers with its recorded list.
        var handler = Github(seen, request => request.RequestUri!.Host == "github.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null, "weir-newest-release-has-no-files");
        var service = new LatestReleaseService(new Factory(handler), [new GitHubReleasesFeed("weir", "jampat000/Weir", IncludePreReleases: true)], new Clock(Start));

        await service.RefreshIfDueAsync("weir", CancellationToken.None);

        Assert.Equal("1.0.0-rc.17", service.Peek("weir")!.Version);
        Assert.Equal(["github.com", "api.github.com"], seen);
    }

    [Fact]
    public async Task When_the_limit_stops_both_routes_it_is_recorded_shown_and_not_asked_again_before_it_lifts()
    {
        var clock = new Clock(Start);
        var reset = Start.AddMinutes(40);
        var calls = 0;
        var handler = new RoutingHandler(request =>
        {
            calls++;
            return request.RequestUri!.Host == "github.com"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : throw new Deluno.Infrastructure.Http.GitHubRateLimitException(reset);
        });
        var service = new LatestReleaseService(new Factory(handler), [new GitHubReleasesFeed("sabnzbd", "sabnzbd/sabnzbd")], clock);

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Null(service.Peek("sabnzbd"));
        Assert.Equal(reset, service.LimitResetsUtc("sabnzbd"));
        Assert.Equal(2, calls);

        clock.Now = Start.AddMinutes(39);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(2, calls);

        clock.Now = reset.AddSeconds(1);
        Assert.Null(service.LimitResetsUtc("sabnzbd"));
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task A_404_from_the_free_page_is_final_the_api_is_not_asked_and_the_problem_is_named()
    {
        var seen = new List<string>();
        // GitHub's recorded 404 for the release feed of a repository that is not there; a call to the API would find no recording and fail the test.
        var handler = Github(seen, request => request.RequestUri!.Host == "api.github.com" ? throw new InvalidOperationException("a private repository answers the API 404 too, and the call still counts") : null, "release-feed-not-found");
        var service = new LatestReleaseService(new Factory(handler), [new GitHubReleasesFeed("weir", "jampat000/no-such-repo-1263", IncludePreReleases: true)], new Clock(Start));

        await service.RefreshIfDueAsync("weir", CancellationToken.None);

        Assert.Null(service.Peek("weir"));
        Assert.Equal(["github.com"], seen);
        Assert.Equal("the release feed isn't reachable", service.CheckProblem("weir"));
        Assert.Null(service.LimitResetsUtc("weir"));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static bool ReleaseVersions_IsOutOfDate(string running, string latest) => Deluno.Contracts.ReleaseVersions.IsOutOfDate(running, latest);

    private static LatestReleaseService Service(CountingHandler handler, Clock clock)
        => new(
            new Factory(handler),
            [
                new GitHubReleasesFeed("sabnzbd", "sabnzbd/sabnzbd"),
                new GitHubReleasesFeed("nzbget", "nzbgetcom/nzbget")
            ],
            clock);

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;
        private int calls;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => this.respond = request => Task.FromResult(respond(request));

        public CountingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            => this.respond = respond;

        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // github.com's release pages are the free route (#1181); these older tests are about the API route, so
            // that page is simply unavailable here (503; a 404 would mean a private repository) and is not counted. The tests below it cover the free route.
            if (request.RequestUri!.Host == "github.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            Interlocked.Increment(ref calls);
            return respond(request);
        }
    }
}
