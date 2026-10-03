using System.Net;
using System.Text;
using Deluno.Connections.Versions;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1117: the newest stable release of each project, read from recorded feed answers and cached for a day. No real network.</summary>
public sealed class LatestReleaseServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GitHub_latest_release_gives_its_version_and_page()
    {
        var release = ReleaseFeedParsers.FromGitHubRelease(
            """{"tag_name":"v5.1.3","name":"5.1.3","draft":false,"prerelease":false,"html_url":"https://github.com/sabnzbd/sabnzbd/releases/tag/v5.1.3"}""");

        Assert.Equal("5.1.3", release!.Version);
        Assert.Equal("https://github.com/sabnzbd/sabnzbd/releases/tag/v5.1.3", release.Url);
    }

    [Theory]
    [InlineData("""{"tag_name":"v5.2.0","draft":false,"prerelease":true,"html_url":"https://github.com/x/y/releases/tag/v5.2.0"}""")]
    [InlineData("""{"tag_name":"v5.2.0","draft":true,"prerelease":false}""")]
    [InlineData("""{"tag_name":"v5.2.0-beta1","draft":false,"prerelease":false}""")]
    [InlineData("""{"message":"Not Found","status":"404"}""")]
    [InlineData("not json")]
    public void A_prerelease_draft_or_unexpected_answer_is_no_release(string body)
        => Assert.Null(ReleaseFeedParsers.FromGitHubRelease(body));

    [Fact]
    public void A_release_page_that_is_not_https_is_not_linked()
        => Assert.Null(ReleaseFeedParsers.FromGitHubRelease("""{"tag_name":"1.2.3","html_url":"javascript:alert(1)"}""")!.Url);

    [Fact]
    public void Qbittorrent_tags_give_the_highest_stable_release()
    {
        var tags = """[{"name":"release-5.3.0rc1"},{"name":"release-5.3.0beta1"},{"name":"release-5.2.4"},{"name":"release-5.2.3"},{"name":"release-5.1.4"},{"name":"v1.0"},{"name":"something-else"}]""";

        var release = ReleaseFeedParsers.FromGitHubTags(tags, "qbittorrent/qBittorrent", "release-");

        Assert.Equal("5.2.4", release!.Version);
        Assert.Equal("https://github.com/qbittorrent/qBittorrent/releases/tag/release-5.2.4", release.Url);
    }

    [Fact]
    public void Deluge_tags_skip_development_tags_and_compare_numbers_not_text()
    {
        var tags = """[{"name":"deluge-2.2.1.dev0"},{"name":"deluge-2.2.0"},{"name":"deluge-2.10.0"},{"name":"deluge-2.1.1.dev0"},{"name":"deluge-2.0.0b1"}]""";

        Assert.Equal("2.10.0", ReleaseFeedParsers.FromGitHubTags(tags, "deluge-torrent/deluge", "deluge-")!.Version);
    }

    [Fact]
    public void Tags_with_nothing_stable_give_no_release()
        => Assert.Null(ReleaseFeedParsers.FromGitHubTags("""[{"name":"release-5.3.0rc1"}]""", "a/b", "release-"));

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
        var handler = new CountingHandler(_ => Ok("""{"tag_name":"v5.0.2","html_url":"https://github.com/x/y/releases/tag/v5.0.2"}"""));
        var service = Service(handler, clock);

        for (var i = 0; i < 5; i++)
        {
            await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        }

        clock.Now = Start.AddHours(23);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("5.0.2", service.Peek("sabnzbd")!.Version);
        Assert.Equal(1, handler.Calls);

        clock.Now = Start.AddHours(24).AddMinutes(1);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Each_project_is_read_separately()
    {
        var handler = new CountingHandler(_ => Ok("""{"tag_name":"1.2.3"}"""));
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
            return Ok("""{"tag_name":"v5.0.2"}""");
        });
        var service = Service(handler, new Clock(Start));

        var first = service.Peek("sabnzbd");
        Assert.Null(first);

        release.SetResult();
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Equal("5.0.2", service.Peek("sabnzbd")!.Version);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_failed_read_is_no_answer_not_an_error_and_is_tried_again_in_an_hour()
    {
        var clock = new Clock(Start);
        var fail = true;
        var handler = new CountingHandler(_ => fail ? throw new HttpRequestException("offline") : Ok("""{"tag_name":"v5.0.2"}"""));
        var service = Service(handler, clock);

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Null(service.Peek("sabnzbd"));

        clock.Now = Start.AddMinutes(30);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        fail = false;
        clock.Now = Start.AddMinutes(61);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        Assert.Equal("5.0.2", service.Peek("sabnzbd")!.Version);
    }

    [Fact]
    public async Task A_rate_limited_answer_keeps_the_last_good_release()
    {
        var clock = new Clock(Start);
        var status = HttpStatusCode.OK;
        var handler = new CountingHandler(_ => status == HttpStatusCode.OK
            ? Ok("""{"tag_name":"v5.0.2"}""")
            : new HttpResponseMessage(status) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") });
        var service = Service(handler, clock);

        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);
        status = HttpStatusCode.Forbidden;
        clock.Now = Start.AddHours(25);
        await service.RefreshIfDueAsync("sabnzbd", CancellationToken.None);

        Assert.Equal("5.0.2", service.Peek("sabnzbd")!.Version);
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

    private const string WeirReleases = """
        [{"tag_name":"v1.0.0-rc.1","draft":false,"prerelease":true,"html_url":"https://github.com/w/weir/releases/tag/v1.0.0-rc.1"},
         {"tag_name":"v1.0.0-rc.2","draft":false,"prerelease":true,"html_url":"https://github.com/w/weir/releases/tag/v1.0.0-rc.2"},
         {"tag_name":"v9.0.0","draft":true,"prerelease":false,"html_url":"https://github.com/w/weir/releases/tag/v9.0.0"}]
        """;

    [Fact]
    public void A_feed_that_includes_pre_releases_takes_the_highest_version_not_the_newest_date_and_skips_drafts()
    {
        var release = ReleaseFeedParsers.FromGitHubReleaseList(WeirReleases);

        Assert.Equal("1.0.0-rc.2", release!.Version);
        Assert.Equal("https://github.com/w/weir/releases/tag/v1.0.0-rc.2", release.Url);
    }

    [Fact]
    public void A_repository_with_only_a_pre_release_resolves_to_it()
        => Assert.Equal("1.0.0-rc.1", ReleaseFeedParsers.FromGitHubReleaseList("""[{"tag_name":"v1.0.0-rc.1","prerelease":true}]""")!.Version);

    [Fact]
    public void A_stable_release_beats_every_rc_of_the_same_numbers()
        => Assert.Equal("1.0.0", ReleaseFeedParsers.FromGitHubReleaseList("""[{"tag_name":"v1.0.0-rc.9"},{"tag_name":"v1.0.0"},{"tag_name":"v1.0.0-rc.10"}]""")!.Version);

    [Theory]
    [InlineData("""[{"tag_name":"v1.0.0","draft":true}]""")]
    [InlineData("[]")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("not json")]
    public void No_usable_release_in_the_list_is_no_release(string body)
        => Assert.Null(ReleaseFeedParsers.FromGitHubReleaseList(body));

    [Fact]
    public void Only_the_feed_that_asks_for_pre_releases_lists_them_and_the_others_stay_on_latest()
    {
        var weir = new GitHubReleasesFeed("weir", "w/weir", IncludePreReleases: true);
        var sab = new GitHubReleasesFeed("sabnzbd", "sabnzbd/sabnzbd");

        Assert.Contains("/releases?", weir.RequestUri.AbsoluteUri);
        Assert.EndsWith("/releases/latest", sab.RequestUri.AbsoluteUri);
        Assert.Equal("1.0.0-rc.2", weir.Pick(WeirReleases)!.Version);
        Assert.Null(sab.Pick("""{"tag_name":"v5.2.0-rc1","draft":false,"prerelease":false}"""));
        Assert.Null(sab.Pick("""{"tag_name":"v5.2.0","draft":false,"prerelease":true}"""));
    }

    [Fact]
    public void A_weir_on_rc1_hears_of_rc2_and_of_the_stable_release_after_it()
    {
        Assert.True(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.1", ReleaseFeedParsers.FromGitHubReleaseList(WeirReleases)!.Version));
        Assert.True(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.2", "1.0.0"));
        Assert.False(Deluno.Contracts.ReleaseVersions.IsOutOfDate("1.0.0-rc.2", ReleaseFeedParsers.FromGitHubReleaseList(WeirReleases)!.Version));
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
            Interlocked.Increment(ref calls);
            return respond(request);
        }
    }
}
