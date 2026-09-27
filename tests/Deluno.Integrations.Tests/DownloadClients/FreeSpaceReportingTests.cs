using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Every torrent client's own free-space reading, folded into its snapshot
/// the same way SABnzbd and NZBGet's already were (#909, #663):
/// qBittorrent's <c>sync/maindata</c>, Transmission's <c>free-space</c> RPC
/// method and Deluge's <c>core.get_free_space</c>. uTorrent's WebUI has no
/// such call at all, so its snapshot reports none rather than a guess.
/// </summary>
public sealed class FreeSpaceReportingTests
{
    [Fact]
    public async Task Qbittorrent_reads_free_space_from_sync_maindata()
    {
        var handler = new QbittorrentHandler(
            torrentsInfo: "[]",
            mainData: """{"server_state":{"free_space_on_disk":42000000000}}""");

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(42_000_000_000L, snapshot!.FreeSpaceBytes);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Qbittorrent_snapshot_is_degraded_when_free_space_is_low()
    {
        var handler = new QbittorrentHandler(
            torrentsInfo: "[]",
            mainData: """{"server_state":{"free_space_on_disk":1000000000}}""");

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("GB free", snapshot.LastHealthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Qbittorrent_reports_no_free_space_when_the_client_will_not_say()
    {
        var handler = new QbittorrentHandler(torrentsInfo: "[]", mainData: "{}");

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.FreeSpaceBytes);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Transmission_reads_free_space_from_the_free_space_rpc_method()
    {
        var handler = new TransmissionHandler(
            torrentGet: """{"result":"success","arguments":{"torrents":[]}}""",
            sessionGet: """{"result":"success","arguments":{"download-dir":"/downloads"}}""",
            freeSpace: """{"result":"success","arguments":{"path":"/downloads","size-bytes":15000000000}}""");

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(TransmissionClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(15_000_000_000L, snapshot!.FreeSpaceBytes);
    }

    [Fact]
    public async Task Transmission_reports_no_free_space_without_a_download_dir_to_ask_about()
    {
        var handler = new TransmissionHandler(
            torrentGet: """{"result":"success","arguments":{"torrents":[]}}""",
            sessionGet: """{"result":"success","arguments":{}}""",
            freeSpace: """{"result":"success","arguments":{"size-bytes":15000000000}}""");

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(TransmissionClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.FreeSpaceBytes);
    }

    [Fact]
    public async Task Deluge_reads_free_space_from_core_get_free_space()
    {
        var handler = new DelugeHandler(
            updateUi: """{"result":{"torrents":{}},"error":null,"id":2}""",
            freeSpace: """{"result":8000000000,"error":null,"id":8}""");

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(DelugeClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(8_000_000_000L, snapshot!.FreeSpaceBytes);
    }

    [Fact]
    public async Task Utorrent_never_reports_free_space()
    {
        var handler = new UTorrentHandler("""{"torrents":[]}""");

        var snapshot = await new UTorrentDownloadClient(() => handler)
            .GetSnapshotAsync(UTorrentClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        // No code change makes this true -- uTorrent's WebUI simply has no
        // free-space call for any adapter to make (#909). This test exists so
        // a future change that tries to guess one breaks loudly instead of
        // shipping a number uTorrent never actually reported.
        Assert.Null(snapshot!.FreeSpaceBytes);
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
        => new(
            Id: "transmission-1", Name: "Transmission", Protocol: "transmission", Host: "localhost", Port: 9091,
            Username: null, Secret: null, EndpointUrl: "http://localhost:9091/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private static DownloadClientItem DelugeClient()
        => new(
            Id: "deluge-1", Name: "Deluge", Protocol: "deluge", Host: "localhost", Port: 8112,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8112/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private static DownloadClientItem UTorrentClient()
        => new(
            Id: "utorrent-1", Name: "uTorrent", Protocol: "utorrent", Host: "localhost", Port: 8081,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8081/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QbittorrentHandler(string torrentsInfo, string mainData) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var reply = path.Contains("auth/login", StringComparison.Ordinal)
                ? "Ok."
                : path.Contains("sync/maindata", StringComparison.Ordinal)
                    ? mainData
                    : path.Contains("torrents/info", StringComparison.Ordinal)
                        ? torrentsInfo
                        : "{}";
            return Task.FromResult(JsonResponse(reply));
        }
    }

    /// <summary>Answers Transmission's three RPC methods a snapshot poll now makes: torrent-get, session-get and free-space.</summary>
    private sealed class TransmissionHandler(string torrentGet, string sessionGet, string freeSpace) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("\"torrent-get\"", StringComparison.Ordinal)
                ? torrentGet
                : body.Contains("\"session-get\"", StringComparison.Ordinal)
                    ? sessionGet
                    : body.Contains("\"free-space\"", StringComparison.Ordinal)
                        ? freeSpace
                        : """{"result":"success","arguments":{}}""";
            return JsonResponse(reply);
        }
    }

    /// <summary>Answers Deluge's JSON-RPC calls: sign-in/connected succeed, then web.update_ui and core.get_free_space by method name.</summary>
    private sealed class DelugeHandler(string updateUi, string freeSpace) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("web.update_ui", StringComparison.Ordinal)
                ? updateUi
                : body.Contains("core.get_free_space", StringComparison.Ordinal)
                    ? freeSpace
                    : """{"result":true,"error":null,"id":1}""";
            return JsonResponse(reply);
        }
    }

    private sealed class UTorrentHandler(string listResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("token.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<div id='token'>test-token</div>", Encoding.UTF8, "text/html")
                });
            }

            var reply = uri.Contains("list=1", StringComparison.Ordinal) ? listResponse : "{}";
            return Task.FromResult(JsonResponse(reply));
        }
    }
}
