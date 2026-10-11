using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Integrations.Search.Trackers;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062: Deluno follows a torrent release's download link itself so it holds
/// the file (and so its hash) before anything is sent to a client.
/// </summary>
public sealed class IndexerTorrentFetcherTests
{
    private static readonly byte[] Torrent = Encoding.Latin1.GetBytes("d4:infod6:lengthi5eee");

    private static IndexerItem Indexer()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new IndexerItem("ix-1", "Example", "torznab", "public", "http://ix.invalid", null, 100, "", "", "both", true, "healthy", null, null, null, null, 0, null, null, now, now);
    }

    [Fact]
    public async Task A_torrent_file_is_read_as_the_answer()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1.torrent?apikey=k", CancellationToken.None);

        Assert.True(fetched.Succeeded);
        Assert.Equal(Torrent, fetched.TorrentFile);
        Assert.Equal("https://ix.example/dl/1.torrent?apikey=k", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task A_redirect_that_ends_in_a_magnet_is_the_answer()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "magnet:?xt=urn:btih:de4158016fbc49ebf35641903cec65a0ad0d30b5");
            return response;
        });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.True(fetched.Succeeded);
        Assert.Equal("magnet:?xt=urn:btih:de4158016fbc49ebf35641903cec65a0ad0d30b5", fetched.MagnetUrl);
        Assert.Null(fetched.TorrentFile);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task A_release_the_indexer_says_is_gone_is_a_dead_release(int status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status));

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.True(fetched.ReleaseIsGone);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task An_indexer_refusing_or_down_is_never_a_bad_release(int status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status));

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
    }

    [Fact]
    public async Task A_plain_text_notice_that_starts_with_a_d_is_a_page_not_a_dead_release()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("daily limit reached") });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Contains("web page instead of a torrent", fetched.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_fetch_says_so_plainly_and_is_marked_as_an_auth_problem(int status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status));

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.True(fetched.AuthFailed);
        Assert.Contains(status.ToString(), fetched.Problem, StringComparison.Ordinal);
        Assert.Contains("API key", fetched.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_web_page_instead_of_a_torrent_is_a_problem_but_not_a_bad_release()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Please sign in</html>") });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Contains("web page instead of a torrent", fetched.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_size_cap_is_refused_without_being_read_to_the_end()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Enumerable.Repeat((byte)'d', IndexerTorrentFetcher.MaxTorrentBytes + 10).ToArray())
        });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.Contains("bigger than a torrent", fetched.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_that_is_not_a_web_address_is_refused()
    {
        var fetched = await Fetcher(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))).FetchAsync(Indexer(), "file:///etc/passwd", CancellationToken.None);

        Assert.False(fetched.Succeeded);
    }

    [Fact]
    public async Task A_cloudflare_check_is_passed_with_the_helpers_clearance_and_the_request_repeated()
    {
        var handler = new StubHandler(request => request.Headers.Contains("Cookie")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) }
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title>") });
        var helper = new Mock<ICloudflareHelper>();
        helper.Setup(instance => instance.IsConfiguredAsync(It.IsAny<CancellationToken>(), It.IsAny<string?>())).ReturnsAsync(true);
        helper.Setup(instance => instance.FetchAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>(), It.IsAny<CloudflareHelperProxy?>(), It.IsAny<string?>()))
            .ReturnsAsync(new CloudflareSolution(new Uri("https://ix.example/dl/1"), HttpStatusCode.OK, "<html/>", "HelperAgent/1", [new Cookie("cf_clearance", "abc")]));

        var fetched = await Fetcher(handler, helper.Object).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.True(fetched.Succeeded, fetched.Problem);
        Assert.Equal(Torrent, fetched.TorrentFile);
        var retried = handler.Requests.Last();
        Assert.Equal("cf_clearance=abc", string.Join(";", retried.Headers.GetValues("Cookie")));
        Assert.Equal("HelperAgent/1", string.Join(" ", retried.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task A_cloudflare_check_with_no_helper_says_so_plainly()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title>") });

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Contains("Cloudflare", fetched.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_indexer_that_does_not_answer_is_a_problem_not_a_bad_release()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var fetched = await Fetcher(handler).FetchAsync(Indexer(), "https://ix.example/dl/1", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Contains("did not answer", fetched.Problem, StringComparison.Ordinal);
    }

    private static IndexerTorrentFetcher Fetcher(StubHandler handler, ICloudflareHelper? helper = null)
        => new(new StubFactory(handler), cloudflareHelper: helper);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// #1062: SABnzbd answering <c>status: true</c> with no id means it took
    /// nothing (usually a discarded duplicate): a plain failure, never a
    /// success nothing could follow.
    /// </summary>
    public sealed class SabnzbdAnswersWithNoId
    {
        // SABnzbd's own recorded answer to an add it discarded as a duplicate (#1237), and two ways its answer could lack the id:
        // the id key gone, and the id empty. Those two are the recorded answer to an ordinary add with the id removed or emptied.
        [Theory]
        [InlineData("duplicate")]
        [InlineData("no-id-key")]
        [InlineData("empty-id")]
        public async Task An_accepted_nzb_with_no_id_is_a_client_that_already_has_it(string shape)
        {
            var added = Deluno.Tests.Recorded.RecordedReplies.Sabnzbd("queued");
            var answer = shape switch
            {
                // SABnzbd's own answer to an add with its job list emptied. (Its real answer to a discarded duplicate is not recorded
                // yet: the capture script now sends the NZB as a file for that, listed in MISSING.md.)
                "duplicate" => added.Body("mode=addurl", ("nzo_ids", new System.Text.Json.Nodes.JsonArray())),
                "no-id-key" => added.Body("mode=addurl", ("nzo_ids", null)),
                _ => added.Body("mode=addurl", ("nzo_ids.0", ""))
            };
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) });
            var client = new DownloadClientItem(
                "sab-1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "key", "http://localhost:8080/", false, null,
                "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            var result = await new SabnzbdDownloadClient(new StubFactory(handler)).GrabAsync(
                client,
                new DownloadClientGrabRequest("Example.Movie.2026", "https://ix.example/getnzb/1", "movies", "movies", "Example"),
                CancellationToken.None);

            // Said as "already has it": the grab service follows it only with a
            // Deluno record of the same release, else fails it as a dead release.
            Assert.True(result.Succeeded);
            Assert.True(result.IsDuplicateFollow);
            Assert.Null(result.ExternalId);
        }

        [Fact]
        public async Task An_accepted_nzb_with_an_id_is_a_send()
        {
            var handler = new StubHandler(_ => Deluno.Tests.Recorded.RecordedReplies.Sabnzbd("queued").Response("mode=addurl", ("nzo_ids.0", "SABnzbd_nzo_abc")));
            var client = new DownloadClientItem(
                "sab-1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "key", "http://localhost:8080/", false, null,
                "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            var result = await new SabnzbdDownloadClient(new StubFactory(handler)).GrabAsync(
                client,
                new DownloadClientGrabRequest("Example.Movie.2026", "https://ix.example/getnzb/1", "movies", "movies", "Example"),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("SABnzbd_nzo_abc", result.ExternalId);
        }
    }
}
