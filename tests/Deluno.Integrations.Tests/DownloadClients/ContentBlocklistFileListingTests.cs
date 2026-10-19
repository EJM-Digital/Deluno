using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #816: each client's own file listing - qBittorrent's
/// <c>api/v2/torrents/files</c>, Transmission's <c>files</c> field on
/// <c>torrent-get</c>, Deluge's <c>core.get_torrent_status</c> - fed to the
/// content blocklist so it can check the files actually inside a download,
/// not only its release name.
/// </summary>
public sealed class ContentBlocklistFileListingTests
{
    [Fact]
    public async Task Qbittorrent_lists_the_files_inside_one_torrent_by_hash()
    {
        var handler = new QbittorrentFilesHandler("""[{"name":"Example.Movie.2026.mkv"},{"name":"Setup.exe"}]""");

        var files = await new QbittorrentDownloadClient(() => handler)
            .GetFilesAsync(QbitClient(), "abc123", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Example.Movie.2026.mkv", "Setup.exe"], files);
    }

    [Fact]
    public async Task Qbittorrent_reports_no_files_when_the_client_will_not_say()
    {
        var handler = new QbittorrentFilesHandler(body: null);

        var files = await new QbittorrentDownloadClient(() => handler)
            .GetFilesAsync(QbitClient(), "abc123", CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task Transmission_lists_files_from_the_files_field_on_torrent_get()
    {
        var handler = new TransmissionFilesHandler(
            """{"result":"success","arguments":{"torrents":[{"files":[{"name":"Example.Movie.2026.mkv"},{"name":"Setup.exe"}]}]}}""");

        var files = await new TransmissionDownloadClient(new Factory(handler))
            .GetFilesAsync(TransmissionClient(), "12", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Example.Movie.2026.mkv", "Setup.exe"], files);
    }

    [Fact]
    public async Task Transmission_reports_no_files_when_the_queue_item_id_does_not_parse()
    {
        var handler = new TransmissionFilesHandler("""{"result":"success","arguments":{"torrents":[]}}""");

        var files = await new TransmissionDownloadClient(new Factory(handler))
            .GetFilesAsync(TransmissionClient(), "not-a-number", CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task Deluge_lists_files_from_get_torrent_status()
    {
        var handler = new DelugeFilesHandler("""{"result":{"files":[{"path":"Example.Movie.2026.mkv"},{"path":"Setup.exe"}]},"error":null,"id":2}""");

        var files = await new DelugeDownloadClient(new Factory(handler))
            .GetFilesAsync(DelugeClient(), "abc123", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Example.Movie.2026.mkv", "Setup.exe"], files);
    }

    [Fact]
    public async Task Utorrent_lists_files_from_getfiles()
    {
        var handler = new UTorrentFilesHandler(
            """{"files":["abc123",[["Example.Movie.2026.mkv",123,123,1],["Setup.exe",456,456,1]]]}""");

        var files = await new UTorrentDownloadClient(() => handler)
            .GetFilesAsync(UTorrentClient(), "abc123", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Example.Movie.2026.mkv", "Setup.exe"], files);
    }

    [Fact]
    public async Task Utorrent_reports_no_files_when_the_client_will_not_say()
    {
        var handler = new UTorrentFilesHandler(body: null);

        var files = await new UTorrentDownloadClient(() => handler)
            .GetFilesAsync(UTorrentClient(), "abc123", CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task Sabnzbd_lists_files_from_get_files()
    {
        // SABnzbd 5.1.3's own mode=get_files reply for a paused job of a film and an .nfo (tests/fixtures/recorded/sabnzbd/5.1.3/files-of-a-paused-job).
        var handler = new SabnzbdFilesHandler(RecordedReplies.Sabnzbd("files-of-a-paused-job").Exchanges[2].Reply.Body!);

        var files = await new SabnzbdDownloadClient(new Factory(handler))
            .GetFilesAsync(SabnzbdClient(), "SABnzbd_nzo_1", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Recorded SABnzbd Files.2008.1080p.WEB-DL.x264-E2E.mkv", "Recorded SABnzbd Files.2008.1080p.WEB-DL.x264-E2E.nfo"], files);
    }

    [Fact]
    public async Task Sabnzbd_lists_no_files_for_a_job_that_has_finished()
    {
        // The same job after it left the queue: the real client answers an empty list, so a finished job has nothing left to strike.
        var handler = new SabnzbdFilesHandler(RecordedReplies.Sabnzbd("files-of-a-finished-job").Exchanges[2].Reply.Body!);

        var files = await new SabnzbdDownloadClient(new Factory(handler))
            .GetFilesAsync(SabnzbdClient(), "SABnzbd_nzo_1", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Empty(files);
    }

    [Fact]
    public async Task Sabnzbd_reports_no_files_when_the_client_will_not_say()
    {
        var handler = new SabnzbdFilesHandler(body: null);

        var files = await new SabnzbdDownloadClient(new Factory(handler))
            .GetFilesAsync(SabnzbdClient(), "SABnzbd_nzo_1", CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task NzbGet_lists_files_from_listfiles()
    {
        // NZBGet 26.3's own listfiles reply for a paused job of a film and an .nfo (tests/fixtures/recorded/nzbget/26.3/files-of-a-paused-job).
        var handler = new NzbGetFilesHandler(RecordedReplies.Nzbget("files-of-a-paused-job").Exchanges[2].Reply.Body!);

        var files = await new NzbGetDownloadClient(new Factory(handler))
            .GetFilesAsync(NzbGetClient(), "42", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(["Recorded NZBGet Files 2.2008.1080p.WEB-DL.x264-E2E.mkv", "Recorded NZBGet Files 2.2008.1080p.WEB-DL.x264-E2E.nfo"], files);
    }

    [Fact]
    public async Task NzbGet_lists_no_files_for_a_job_that_has_finished()
    {
        var handler = new NzbGetFilesHandler(RecordedReplies.Nzbget("files-of-a-finished-job").Exchanges[2].Reply.Body!);

        var files = await new NzbGetDownloadClient(new Factory(handler))
            .GetFilesAsync(NzbGetClient(), "42", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Empty(files);
    }

    [Fact]
    public async Task NzbGet_reports_no_files_when_the_queue_item_id_does_not_parse()
    {
        var files = await new NzbGetDownloadClient(new Factory(new NeverCalledHandler()))
            .GetFilesAsync(NzbGetClient(), "not-a-number", CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task Rtorrent_lists_files_from_f_multicall()
    {
        // rTorrent 0.16.25's own f.multicall reply for a release folder of three files (tests/fixtures/recorded/rtorrent/0.16.25/finished-folder).
        var handler = new RtorrentFilesHandler(RecordedReplies.Rtorrent("finished-folder").Exchanges[3].Reply.Body!);

        var files = await new RtorrentDownloadClient(new Factory(handler))
            .GetFilesAsync(RtorrentClient(), "abc123hash", CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(
            ["Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E01.mkv", "Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E02.mkv", "Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E03.mkv"],
            files);
    }

    [Fact]
    public async Task Rtorrent_reports_no_files_when_the_call_fails()
    {
        var handler = new RtorrentFaultHandler();

        var files = await new RtorrentDownloadClient(new Factory(handler))
            .GetFilesAsync(RtorrentClient(), "abc123hash", CancellationToken.None);

        Assert.Null(files);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static DownloadClientItem QbitClient()
        => new(
            Id: "qbit-1", Name: "qBittorrent", Protocol: "qbittorrent", Host: "localhost", Port: 8080,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8080/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private static DownloadClientItem TransmissionClient()
        => QbitClient() with { Id = "transmission-1", Name = "Transmission", Protocol = "transmission" };

    private static DownloadClientItem DelugeClient()
        => QbitClient() with { Id = "deluge-1", Name = "Deluge", Protocol = "deluge" };

    private static DownloadClientItem UTorrentClient()
        => QbitClient() with { Id = "utorrent-1", Name = "uTorrent", Protocol = "utorrent" };

    private static DownloadClientItem SabnzbdClient()
        => QbitClient() with { Id = "sab-1", Name = "SABnzbd", Protocol = "sabnzbd", Secret = "sab-api-key" };

    private static DownloadClientItem NzbGetClient()
        => QbitClient() with { Id = "nzbget-1", Name = "NZBGet", Protocol = "nzbget" };

    private static DownloadClientItem RtorrentClient()
        => QbitClient() with { Id = "rtorrent-1", Name = "rTorrent", Protocol = "rtorrent" };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QbittorrentFilesHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("auth/login", StringComparison.Ordinal)) return Task.FromResult(JsonResponse("Ok."));
            if (path.Contains("torrents/files", StringComparison.Ordinal))
            {
                return body is null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                    : Task.FromResult(JsonResponse(body));
            }
            return Task.FromResult(JsonResponse("{}"));
        }
    }

    private sealed class TransmissionFilesHandler(string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return JsonResponse(body.Contains("\"torrent-get\"", StringComparison.Ordinal) ? reply : """{"result":"success","arguments":{}}""");
        }
    }

    private sealed class DelugeFilesHandler(string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(body.Contains("core.get_torrent_status", StringComparison.Ordinal) ? reply : """{"result":true,"error":null,"id":1}""");
        }
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This call must not reach the network.");
    }

    private sealed class SabnzbdFilesHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (!query.Contains("mode=get_files", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{}"));
            }

            return body is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Task.FromResult(JsonResponse(body));
        }
    }

    private sealed class UTorrentFilesHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("token.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><div id='token'>test-token</div></html>", Encoding.UTF8, "text/html")
                });
            }

            var query = request.RequestUri?.Query ?? string.Empty;
            if (!query.Contains("action=getfiles", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{}"));
            }

            return body is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Task.FromResult(JsonResponse(body));
        }
    }

    private sealed class NzbGetFilesHandler(string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return JsonResponse(body.Contains("\"listfiles\"", StringComparison.Ordinal) ? reply : """{"result":null}""");
        }
    }

    private sealed class RtorrentFilesHandler(string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var xml = body.Contains("<methodName>f.multicall</methodName>", StringComparison.Ordinal)
                ? reply
                : RecordedReplies.Rtorrent("idle").Exchanges[1].Reply.Body!;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
        }
    }

    private sealed class RtorrentFaultHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
