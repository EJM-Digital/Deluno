using System.Net;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301, 6-torrent-fetch-at (tf10, tf11, tf12): Deluno following a torrent release's download link itself. The links are served over real loopback
/// sockets with a real HttpClient, so a redirect to another address is really followed. The torrent file is a real one (the very bytes the recorded
/// rTorrent was sent), and the headers of a good answer are Jackett 0.24.2850's own (tests/fixtures/recorded/jackett). No client is involved.
/// </summary>
public sealed class IndexerTorrentFetcherLinksProxiesAndLimitsTests
{
    private static IndexerItem Indexer(string? proxyId = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new IndexerItem("ix-1", "Jackett YTS", "torznab", "public", "http://ix.invalid", null, 100, "", "", "both", true, "healthy", null, null, null, null, 0, null, null, now, now)
        {
            ProxyId = proxyId
        };
    }

    /// <summary>The torrent file the recorded rTorrent was sent: real bytes, so the file is a real torrent.</summary>
    private static byte[] RealTorrent()
        => Convert.FromBase64String(Regex.Match(RecordedReplies.Rtorrent("add-file").Exchanges[1].Request.Body!, "<base64>([^<]+)</base64>").Groups[1].Value);

    /// <summary>A good answer to a download link: Jackett's recorded reply (status and headers), carrying a real torrent file.</summary>
    private static RecordedReply JackettTorrentAnswer()
    {
        var recorded = RecordedReplies.Jackett("download-link").Single.Reply;
        Assert.Equal(200, recorded.Status);
        Assert.Equal("application/x-bittorrent", recorded.Headers["content-type"]);
        return recorded with { Body = null, Bytes = RealTorrent() };
    }

    private sealed class Factory(Func<HttpClient> make) : IHttpClientFactory
    {
        public List<HttpClient> Made { get; } = [];

        public HttpClient CreateClient(string name)
        {
            var client = make();
            Made.Add(client);
            return client;
        }
    }

    /// <summary>The default handler the "indexers" client has (it follows redirects), over the real network stack.</summary>
    private static Factory RealClients() => new(() => new HttpClient(new SocketsHttpHandler(), disposeHandler: true));

    private sealed class Throwing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    /// <summary>A little web server on a free loopback port answering each request with whatever the test says.</summary>
    private sealed class Site : IDisposable
    {
        private readonly HttpListener _listener = new();

        public Site(Func<HttpListenerRequest, (int Status, string? Location, IDictionary<string, string>? Headers, byte[]? Body)> answer)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    Requests.Add(context.Request.Url!.PathAndQuery);
                    var (status, location, headers, body) = answer(context.Request);
                    context.Response.StatusCode = status;
                    if (location is not null) context.Response.RedirectLocation = location;
                    foreach (var header in headers ?? new Dictionary<string, string>())
                    {
                        if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = header.Value;
                        else context.Response.AddHeader(header.Key, header.Value);
                    }

                    if (body is not null) await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public int Port { get; }

        public string Address => $"http://127.0.0.1:{Port}";

        public List<string> Requests { get; } = [];

        public void Dispose() => _listener.Close();
    }

    // ---- tf10: a redirect to another address, 429, the timeout ----

    [Fact]
    public async Task A_normal_redirect_to_another_address_is_followed_and_the_api_key_in_the_address_goes_with_it()
    {
        var answer = JackettTorrentAnswer();
        using var cdn = new Site(_ => (200, null, answer.Headers.ToDictionary(h => h.Key, h => h.Value), answer.Bytes));
        using var indexer = new Site(_ => (302, $"{cdn.Address}/files/one.torrent?jackett_apikey=KEY&path=abc", null, null));

        var fetched = await new IndexerTorrentFetcher(RealClients()).FetchAsync(Indexer(), $"{indexer.Address}/dl/yts/?jackett_apikey=KEY&path=abc", CancellationToken.None);

        Assert.True(fetched.Succeeded, fetched.Problem);
        Assert.Equal(RealTorrent(), fetched.TorrentFile);
        Assert.Null(fetched.MagnetUrl);
        Assert.Equal("/dl/yts/?jackett_apikey=KEY&path=abc", Assert.Single(indexer.Requests));
        Assert.Equal("/files/one.torrent?jackett_apikey=KEY&path=abc", Assert.Single(cdn.Requests));
    }

    [Fact]
    public async Task A_429_with_Retry_After_is_a_plain_refusal_that_is_not_a_bad_release_and_not_a_wrong_key()
    {
        using var indexer = new Site(_ => (429, null, new Dictionary<string, string> { ["Retry-After"] = "120" }, []));

        var fetched = await new IndexerTorrentFetcher(RealClients()).FetchAsync(Indexer(), $"{indexer.Address}/dl/yts/?path=abc", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.False(fetched.AuthFailed);
        Assert.Contains("429", fetched.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fetch_waits_twenty_seconds_at_most_and_a_timeout_is_a_problem_not_a_bad_release()
    {
        var factory = new Factory(() => new HttpClient(new Throwing(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())), disposeHandler: true));

        var fetched = await new IndexerTorrentFetcher(factory).FetchAsync(Indexer(), "http://ix.invalid/dl/yts/?path=abc", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(factory.Made).Timeout);
        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Contains("did not answer", fetched.Problem, StringComparison.Ordinal);
    }

    // ---- tf12: what Jackett really answers a link with ----

    [Fact]
    public async Task A_good_Jackett_answer_with_the_torrent_content_type_is_read_as_the_torrent_file()
    {
        var answer = JackettTorrentAnswer();
        using var site = new Site(_ => (answer.Status, null, answer.Headers.ToDictionary(h => h.Key, h => h.Value), answer.Bytes));

        var fetched = await new IndexerTorrentFetcher(RealClients()).FetchAsync(Indexer(), $"{site.Address}/dl/yts/?path=abc", CancellationToken.None);

        Assert.True(fetched.Succeeded, fetched.Problem);
        Assert.Equal(RealTorrent(), fetched.TorrentFile);
    }

    [Fact]
    public async Task Jackett_answering_500_for_a_tracker_it_has_not_set_up_is_a_problem_with_the_indexer_and_never_a_dead_release()
    {
        var reply = RecordedReplies.Jackett("unknown-indexer").Single.Reply;
        Assert.Equal(500, reply.Status);
        using var site = new Site(_ => (reply.Status, null, reply.Headers.ToDictionary(h => h.Key, h => h.Value), System.Text.Encoding.UTF8.GetBytes(reply.Body ?? string.Empty)));

        var fetched = await new IndexerTorrentFetcher(RealClients()).FetchAsync(Indexer(), $"{site.Address}/dl/nowhere/?path=abc", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.False(fetched.AuthFailed);
        Assert.Contains("500", fetched.Problem, StringComparison.Ordinal);
    }

    // ---- tf11: the indexer's assigned proxy, and the Cloudflare helper picked by the proxy's kind ----

    private static ProxyItem Proxy(string kind, string address, string? username = null, string? password = null)
        => new("proxy-1", "Assigned", kind, address, username, password, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static Mock<IConnectionsRepository> Repository(ProxyItem proxy)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListProxiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([proxy]);
        return repository;
    }

    private sealed class SeenHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task An_indexer_with_a_proxy_is_fetched_through_that_proxys_client_and_never_the_direct_one()
    {
        var proxy = Proxy(ProxyTypes.Http, "proxy.example:3128", "user", "secret");
        var viaProxy = new SeenHandler(_ => { var r = JackettTorrentAnswer(); return r.ToResponse(); });
        var proxyFactory = new Mock<IIndexerProxyHttpClientFactory>();
        proxyFactory.Setup(instance => instance.CreateClient(It.Is<ProxyItem?>(p => p != null && p.Id == "proxy-1"))).Returns(() => new HttpClient(viaProxy, disposeHandler: false));
        var direct = new Factory(() => new HttpClient(new Throwing(new InvalidOperationException("the direct client must not be used")), disposeHandler: true));

        var fetched = await new IndexerTorrentFetcher(direct, Repository(proxy).Object, proxyFactory.Object)
            .FetchAsync(Indexer("proxy-1"), "https://ix.example/dl/yts/?path=abc", CancellationToken.None);

        Assert.True(fetched.Succeeded, fetched.Problem);
        Assert.Equal(RealTorrent(), fetched.TorrentFile);
        Assert.Equal("https://ix.example/dl/yts/?path=abc", Assert.Single(viaProxy.Seen).RequestUri!.ToString());
        Assert.Empty(direct.Made);
    }

    [Fact]
    public async Task A_cloudflare_check_behind_a_FlareSolverr_proxy_asks_that_proxys_address_as_the_helper_and_passes_no_proxy()
    {
        var proxy = Proxy(ProxyTypes.FlareSolverr, "flaresolverr.example:8191");
        var (helper, fetched) = await RunCloudflareAsync(proxy);

        Assert.True(fetched.Succeeded, fetched.Problem);
        helper.Verify(instance => instance.IsConfiguredAsync(It.IsAny<CancellationToken>(), "http://flaresolverr.example:8191"), Times.Once);
        helper.Verify(instance => instance.FetchAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>(), null, "http://flaresolverr.example:8191"), Times.Once);
    }

    [Fact]
    public async Task A_cloudflare_check_behind_an_ordinary_proxy_passes_the_proxy_with_its_login_to_the_helper_and_no_helper_address()
    {
        var proxy = Proxy(ProxyTypes.Socks5, "socks.example:1080", "user", "secret");
        var (helper, fetched) = await RunCloudflareAsync(proxy);

        Assert.True(fetched.Succeeded, fetched.Problem);
        helper.Verify(instance => instance.IsConfiguredAsync(It.IsAny<CancellationToken>(), null), Times.Once);
        helper.Verify(instance => instance.FetchAsync(
            It.IsAny<Uri>(), It.IsAny<CancellationToken>(),
            It.Is<CloudflareHelperProxy?>(p => p != null && p.Url == "socks5://socks.example:1080" && p.Username == "user" && p.Password == "secret"), null), Times.Once);
    }

    private static async Task<(Mock<ICloudflareHelper> Helper, IndexerTorrentFetch Fetched)> RunCloudflareAsync(ProxyItem proxy)
    {
        var answers = new SeenHandler(request => request.Headers.Contains("Cookie")
            ? JackettTorrentAnswer().ToResponse()
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title>") });
        var proxyFactory = new Mock<IIndexerProxyHttpClientFactory>();
        proxyFactory.Setup(instance => instance.CreateClient(It.IsAny<ProxyItem?>())).Returns(() => new HttpClient(answers, disposeHandler: false));
        var helper = new Mock<ICloudflareHelper>();
        helper.Setup(instance => instance.IsConfiguredAsync(It.IsAny<CancellationToken>(), It.IsAny<string?>())).ReturnsAsync(true);
        helper.Setup(instance => instance.FetchAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>(), It.IsAny<CloudflareHelperProxy?>(), It.IsAny<string?>()))
            .ReturnsAsync(new CloudflareSolution(new Uri("https://ix.example/dl/1"), HttpStatusCode.OK, "<html/>", "HelperAgent/1", [new Cookie("cf_clearance", "abc")]));

        var fetched = await new IndexerTorrentFetcher(new Factory(() => new HttpClient(new Throwing(new InvalidOperationException("direct")), disposeHandler: true)), Repository(proxy).Object, proxyFactory.Object, helper.Object)
            .FetchAsync(Indexer("proxy-1"), "https://ix.example/dl/1", CancellationToken.None);
        return (helper, fetched);
    }
}
