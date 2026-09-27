using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
    public async Task Utorrent_and_usenet_clients_do_not_support_file_listing_yet()
    {
        // #816: "where available" - uTorrent's WebUI, SABnzbd and NZBGet have
        // no file-listing call wired up in this pass, so the base
        // implementation's "unknown" answer is what a caller sees, and the
        // content blocklist falls back to the release name for them.
        var files = await new UTorrentDownloadClient(() => new NeverCalledHandler())
            .GetFilesAsync(UTorrentClient(), "abc123", CancellationToken.None);

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
            => throw new InvalidOperationException("uTorrent's base GetFilesAsync must not make an HTTP call.");
    }
}
