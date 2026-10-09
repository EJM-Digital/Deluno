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

    private sealed class Page(string? atom) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(atom is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
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
