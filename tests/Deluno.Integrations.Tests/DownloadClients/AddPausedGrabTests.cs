using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #805 — a download client with AddPaused set sends the paused/initial-state
/// flag its own protocol supports on every grab, instead of always starting
/// the download active.
/// </summary>
public sealed class AddPausedGrabTests
{
    [Theory]
    [InlineData(false, "paused=false")]
    [InlineData(true, "paused=true")]
    public async Task Qbittorrent_grab_sends_the_clients_AddPaused_flag(bool addPaused, string expected)
    {
        var bodies = new List<string>();
        // The stub answers torrents/info with the same empty list before and
        // after the add, so the grab itself is reported as not taken (#606's
        // own before/after honesty check) — irrelevant here, since only the
        // request body sent to torrents/add is under test.
        await new QbittorrentDownloadClient(() => new QbittorrentHandler(bodies))
            .GrabAsync(Client(addPaused), Request(), CancellationToken.None);

        var body = Assert.Single(bodies);
        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transmission_grab_sends_the_clients_AddPaused_flag(bool addPaused)
    {
        var sent = new List<string>();
        await new TransmissionDownloadClient(new StubFactory(new TransmissionHandler(sent)))
            .GrabAsync(Client(addPaused) with { Protocol = "transmission" }, Request(), CancellationToken.None);

        // A category also triggers a session-get lookup first (#629); only the
        // torrent-add call itself carries the paused flag under test.
        var addBody = sent.Single(body => body.Contains("\"torrent-add\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(addBody);
        Assert.Equal(addPaused, document.RootElement.GetProperty("arguments").GetProperty("paused").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deluge_grab_sends_the_clients_AddPaused_flag(bool addPaused)
    {
        var sent = new List<string>();
        await new DelugeDownloadClient(new StubFactory(new DelugeHandler(sent)))
            .GrabAsync(Client(addPaused) with { Protocol = "deluge" }, Request(), CancellationToken.None);

        // Two calls land here: the add itself and the follow-up label set.
        // The add is the JSON-RPC call naming one of Deluge's add methods.
        var addBody = sent.First(body => body.Contains("core.add_torrent_", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(addBody);
        var options = document.RootElement.GetProperty("params")[1];
        Assert.Equal(addPaused, options.GetProperty("add_paused").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sabnzbd_grab_with_AddPaused_uses_SABnzbds_own_paused_priority(bool addPaused)
    {
        string? query = null;
        var handler = new SabHandler(request => query = request.RequestUri?.Query);
        await new SabnzbdDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(addPaused) with { Protocol = "sabnzbd", Secret = "api-key" }, Request(), CancellationToken.None);

        if (addPaused)
        {
            Assert.Contains("priority=-2", query, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("priority=", query, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NzbGet_grab_sends_the_clients_AddPaused_flag(bool addPaused)
    {
        var sent = new List<string>();
        await new NzbGetDownloadClient(new StubFactory(new NzbGetHandler(sent)))
            .GrabAsync(Client(addPaused) with { Protocol = "nzbget" }, Request(), CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(sent));
        var parameters = document.RootElement.GetProperty("params");
        // append(NZBFilename, Content, Category, Priority, AddToTop, AddPaused, ...)
        Assert.Equal(addPaused, parameters[5].GetBoolean());
    }

    // ------------------------------------------------------------------ fixtures

    private static DownloadClientGrabRequest Request()
        => new("Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO", "http://indexer.test/download/1", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem Client(bool addPaused)
        => new(
            "client-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            AddPaused: addPaused);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class QbittorrentHandler(List<string> addBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                addBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                return Text("Ok.");
            }

            if (path.Contains("auth/login", StringComparison.Ordinal)) return Text("Ok.");
            // Reads as "already added" (same hash list before/after) which is
            // fine here: only the request body sent to torrents/add matters.
            if (path.Contains("torrents/info", StringComparison.Ordinal)) return Json("[]");
            return Text("Ok.");
        }

        private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class TransmissionHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            var reply = body.Contains("\"torrent-add\"", StringComparison.Ordinal)
                ? """{"result":"success","arguments":{"torrent-added":{"id":7,"hashString":"abc"}}}"""
                : """{"result":"success","arguments":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class DelugeHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            var answer = body.Contains("core.add_torrent_", StringComparison.Ordinal)
                ? """{"result":"abcd1234abcd1234abcd1234abcd1234abcd1234","error":null,"id":2}"""
                : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SabHandler(Action<HttpRequestMessage> observe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":true,"nzo_ids":["nzo-1"]}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NzbGetHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"1.1","result":42}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
