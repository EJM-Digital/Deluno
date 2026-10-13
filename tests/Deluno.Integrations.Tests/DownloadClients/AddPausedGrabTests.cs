using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

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
        // qBittorrent's own recorded sign-in, add and list replies. Only the request body sent to torrents/add is under test.
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("finished-single-file")],
            (request, body) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("torrents/add", StringComparison.Ordinal)) bodies.Add(body);
                return null;
            });
        await new QbittorrentDownloadClient(() => handler)
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
        await new TransmissionDownloadClient(new StubFactory(Capture("transmission", sent, RecordedReplies.Transmission("settings"), RecordedReplies.Transmission("finished-single-file"))))
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
        await new DelugeDownloadClient(new StubFactory(Capture("deluge", sent, RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("magnet-no-metadata"), RecordedReplies.Deluge("label-set"))))
            .GrabAsync(Client(addPaused) with { Protocol = "deluge" }, Request() with { DownloadUrl = Magnet }, CancellationToken.None);

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
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("queued")], (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("mode=addurl", StringComparison.Ordinal))
            {
                query = request.RequestUri.Query;
                // The recorded reply to an add by address; its address is the recording's own, so it is asked for by the mode.
                return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
            }

            return null;
        });
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
        await new NzbGetDownloadClient(new StubFactory(Capture("nzbget", sent, RecordedReplies.Nzbget("queued"))))
            .GrabAsync(Client(addPaused) with { Protocol = "nzbget" }, Request(), CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(sent));
        var parameters = document.RootElement.GetProperty("params");
        // append(NZBFilename, Content, Category, Priority, AddToTop, AddPaused, ...)
        Assert.Equal(addPaused, parameters[5].GetBoolean());
    }

    // ------------------------------------------------------------------ fixtures

    private const string Magnet = "magnet:?xt=urn:btih:8d878a21c3d4180ae08b4e044904667d9f30bec5&dn=Big.Buck.Bunny";

    /// <summary>A client answering from the given recordings, keeping the body of every call it gets.</summary>
    private static HttpMessageHandler Capture(string app, List<string> sent, params Recording[] recordings)
        => RecordedReplies.Handler(app, recordings, (_, body) =>
        {
            sent.Add(body);
            return null;
        });

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
}
