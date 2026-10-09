using System.Net;
using Deluno.Tray;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1181: the app's own update check finds the newest tag on github.com's release feed, not through the API that GitHub limits to 60 calls an hour. No real network.</summary>
public sealed class ReleaseFeedUpdateSourceTests
{
    private const string Atom = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry><link rel="alternate" type="text/html" href="https://github.com/owner/app/releases/tag/v1.0.0-rc.2"/></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/owner/app/releases/tag/v1.0.0-rc.10"/></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/owner/app/releases/tag/v0.9.0"/></entry>
        </feed>
        """;

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
        var handler = new Page(Atom);

        Assert.Equal("v1.0.0-rc.10", await ReleaseFeedUpdateSource.FindNewestTagAsync("owner/app", includePreReleases: true, handler, CancellationToken.None));
        Assert.Equal("v0.9.0", await ReleaseFeedUpdateSource.FindNewestTagAsync("owner/app", includePreReleases: false, handler, CancellationToken.None));
        Assert.All(handler.Requested, url => Assert.Equal("https://github.com/owner/app/releases.atom", url));
    }

    [Fact]
    public async Task An_unreadable_feed_is_no_tag_so_the_caller_can_fall_back()
        => Assert.Null(await ReleaseFeedUpdateSource.FindNewestTagAsync("owner/app", true, new Page(null), CancellationToken.None));

    [Fact]
    public async Task The_update_files_are_read_from_the_newest_tags_download_folder_and_the_api_is_not_asked()
    {
        var downloader = new RecordingDownloader();
        var source = new ReleaseFeedUpdateSource("https://github.com/owner/app", includePreReleases: true, downloader, new Page(Atom));

        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "Deluno", "win");

        Assert.Single(feed.Assets);
        var url = Assert.Single(downloader.Strings);
        Assert.StartsWith("https://github.com/owner/app/releases/download/v1.0.0-rc.10/releases.win.json", url);
        Assert.DoesNotContain("api.github.com", url);
    }

    [Fact]
    public async Task A_404_from_the_release_feed_is_final_and_the_api_is_never_asked()
    {
        var handler = new Page(null, HttpStatusCode.NotFound);
        var source = new ReleaseFeedUpdateSource("https://github.com/owner/private", includePreReleases: true, new RecordingDownloader(), handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetReleaseFeed(new NullVelopackLogger(), "Deluno", "win"));

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.True(UpdateFeedErrors.IsNotFound(failure));
        var only = Assert.Single(handler.Requested);
        Assert.Equal("https://github.com/owner/private/releases.atom", only);
        Assert.Equal((null, true), await ReleaseFeedUpdateSource.LookUpNewestTagAsync("owner/private", true, handler, CancellationToken.None));
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
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
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

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Task.FromResult(Array.Empty<byte>());

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => throw new InvalidOperationException("The API listing must not use the ordinary downloader.");
    }

    private sealed class Page(string? atom, HttpStatusCode failure = HttpStatusCode.ServiceUnavailable) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(atom is null
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
            return Task.FromResult("""{"Assets":[{"PackageId":"Deluno","Version":"1.0.0-rc.10","Type":"Full","FileName":"Deluno-1.0.0-rc.10-full.nupkg","SHA1":"0000000000000000000000000000000000000000","SHA256":"","Size":1}]}""");
        }
    }
}
