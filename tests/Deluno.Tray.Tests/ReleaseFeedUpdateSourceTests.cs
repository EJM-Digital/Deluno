using System.Net;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1181: the app's own update check finds the newest tag on github.com's release feed, not through the API that GitHub limits to 60 calls an hour. No real network.</summary>
public sealed class ReleaseFeedUpdateSourceTests
{
    // GitHub's own release feeds, recorded on 14 Oct 2026 (tests/fixtures/recorded/github): Weir's (release candidates only), NZBGet's
    // (a rolling "testing" tag ahead of v26.3) and the 404 for a repository that is not there.
    private static string WeirAtom() => RecordedReplies.Github("weir-newest-release-has-no-files").Find("Weir/releases.atom").Reply.Body!;

    private static string NzbgetAtom() => RecordedReplies.Github("nzbget-release-feed").Single.Reply.Body!;

    [Theory]
    [InlineData("https://github.com/owner/app", "owner/app")]
    [InlineData("https://github.com/owner/app/", "owner/app")]
    [InlineData("https://github.com/owner/app.git", "owner/app")]
    [InlineData("https://example.test/owner/app", null)]
    [InlineData("not a url", null)]
    public void The_repository_is_read_from_the_update_source_address(string url, string? expected)
        => Assert.Equal(expected, ReleaseFeedUpdateSource.RepositoryOf(url));

    [Fact]
    public async Task The_newest_tag_counts_release_candidates_by_version_order_only_for_a_channel_that_wants_them()
    {
        var weir = new Page(WeirAtom());

        Assert.Equal("v1.0.0-rc.17", await ReleaseFeedUpdateSource.FindNewestTagAsync("jampat000/Weir", includePreReleases: true, weir, CancellationToken.None));
        // Weir has published release candidates only, so a channel that wants stable releases finds none.
        Assert.Null(await ReleaseFeedUpdateSource.FindNewestTagAsync("jampat000/Weir", includePreReleases: false, weir, CancellationToken.None));
        Assert.All(weir.Requested, url => Assert.Equal("https://github.com/jampat000/Weir/releases.atom", url));

        var nzbget = new Page(NzbgetAtom());
        Assert.Equal("v26.3", await ReleaseFeedUpdateSource.FindNewestTagAsync("nzbgetcom/nzbget", includePreReleases: true, nzbget, CancellationToken.None));
        Assert.Equal("v26.3", await ReleaseFeedUpdateSource.FindNewestTagAsync("nzbgetcom/nzbget", includePreReleases: false, nzbget, CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_feed_is_no_tag_so_the_caller_can_fall_back()
        => Assert.Null(await ReleaseFeedUpdateSource.FindNewestTagAsync("owner/app", true, new Page(null), CancellationToken.None));

    [Fact]
    public async Task The_update_files_are_read_from_the_newest_tags_download_folder_and_the_api_is_not_asked()
    {
        var downloader = new RecordingDownloader();
        var source = new ReleaseFeedUpdateSource("https://github.com/nzbgetcom/nzbget", includePreReleases: true, downloader, new Page(NzbgetAtom()));

        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "Deluno", "win");

        Assert.NotEmpty(feed.Assets);
        var url = Assert.Single(downloader.Strings);
        Assert.StartsWith("https://github.com/nzbgetcom/nzbget/releases/download/v26.3/releases.win.json", url);
        Assert.DoesNotContain("api.github.com", url);
    }

    [Fact]
    public async Task A_404_from_the_release_feed_is_final_and_the_api_is_never_asked()
    {
        var handler = new Page(null, reply: RecordedReplies.Github("release-feed-not-found").Single.Reply);
        var source = new ReleaseFeedUpdateSource("https://github.com/jampat000/no-such-repo-1263", includePreReleases: true, new RecordingDownloader(), handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetReleaseFeed(new NullVelopackLogger(), "Deluno", "win"));

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.True(UpdateFeedErrors.IsNotFound(failure));
        var only = Assert.Single(handler.Requested);
        Assert.Equal("https://github.com/jampat000/no-such-repo-1263/releases.atom", only);
        Assert.Equal((null, true), await ReleaseFeedUpdateSource.LookUpNewestTagAsync("jampat000/no-such-repo-1263", true, handler, CancellationToken.None));
    }

    [Fact]
    public async Task The_api_fallback_reuses_a_304_then_records_the_limit_says_it_in_the_servers_sentence_and_makes_no_call_before_the_reset()
    {
        var state = new Deluno.Infrastructure.Http.GitHubApiState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(40);
        var api = new Api(reset);
        // The release feed cannot be read (503), so each check falls back to the API.
        var source = new ReleaseFeedUpdateSource("https://github.com/owner/app", includePreReleases: true, new ApiRecordingDownloader(), new Page(null), api, state);
        var logger = new NullVelopackLogger();

        await source.GetReleaseFeed(logger, "Deluno", "win");
        await source.GetReleaseFeed(logger, "Deluno", "win");

        Assert.Equal(2, api.Calls);
        Assert.Null(api.IfNoneMatch[0]);
        Assert.Equal("\"e1\"", api.IfNoneMatch[1]);

        var limited = await Assert.ThrowsAnyAsync<Exception>(() => source.GetReleaseFeed(logger, "Deluno", "win"));
        var limit = Assert.IsType<Deluno.Infrastructure.Http.GitHubRateLimitException>(GithubApiClient.LimitOf(limited));
        Assert.Equal(Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor(reset), UpdateFeedErrors.CheckFailedMessage(limited));
        Assert.Equal(limit.Message, UpdateFeedErrors.CheckFailedMessage(limited));
        var calls = api.Calls;

        await Assert.ThrowsAnyAsync<Exception>(() => source.GetReleaseFeed(logger, "Deluno", "win"));
        Assert.Equal(calls, api.Calls);
    }

    private sealed class Api(DateTimeOffset reset) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string?> IfNoneMatch { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            IfNoneMatch.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            if (Calls == 1)
            {
                var ok = RecordedReplies.Github("weir-newest-release-has-no-files").Find("api.github.com").Reply.ToResponse();
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"");
                return Task.FromResult(ok);
            }

            if (Calls == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return Task.FromResult(limited);
        }
    }

    /// <summary>The ordinary downloader: only update files; the API listing never comes here.</summary>
    private sealed class ApiRecordingDownloader : IFileDownloader
    {
        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
            => Task.CompletedTask;

        // The release's Velopack feed file, as GitHub served Weir's.
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(RecordedReplies.Github("weir-velopack-feed").Single.Reply.Body!));

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => throw new InvalidOperationException("The API listing must not use the ordinary downloader.");
    }

    /// <summary>Answers every call with GitHub's recorded feed (or recorded reply); with neither, with the plain 503 of a feed that cannot be read (a status with no body, which is not recorded).</summary>
    private sealed class Page(string? atom, HttpStatusCode failure = HttpStatusCode.ServiceUnavailable, RecordedReply? reply = null) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(reply is not null
                ? reply.ToResponse()
                : atom is null
                    ? new HttpResponseMessage(failure)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(atom) });
        }
    }

    private sealed class RecordingDownloader : IFileDownloader
    {
        public List<string> Strings { get; } = [];

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
            => Task.CompletedTask;

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Task.FromResult(Array.Empty<byte>());

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Strings.Add(url);
            // The Velopack feed an app built with Velopack publishes beside its installer: Weir's own, as GitHub served it.
            return Task.FromResult(RecordedReplies.Github("weir-velopack-feed").Single.Reply.Body!);
        }
    }
}
