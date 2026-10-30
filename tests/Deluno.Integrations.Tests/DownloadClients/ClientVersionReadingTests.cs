using System.Net;
using Deluno.Tests.Recorded;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1117: every torrent client reads its own version on the poll it already makes, from a recorded answer, and
/// says nothing when it will not tell. SABnzbd and NZBGet were already covered by the usenet parity tests.
/// </summary>
public sealed class ClientVersionReadingTests
{
    [Fact]
    public async Task Qbittorrent_reads_its_version_from_app_version()
    {
        var snapshot = await new QbittorrentDownloadClient(() => new RoutedHandler(
            ("torrents/info", "[]"), ("sync/maindata", """{"server_state":{"free_space_on_disk":42000000000}}"""), ("app/version", "v4.6.7")))
            .GetSnapshotAsync(Client("qbittorrent"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("4.6.7", snapshot!.ClientVersion);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Qbittorrent_whose_version_call_is_refused_still_gives_a_snapshot_and_no_version()
    {
        var snapshot = await new QbittorrentDownloadClient(() => new RoutedHandler(
            ("torrents/info", "[]"), ("sync/maindata", """{"server_state":{"free_space_on_disk":42000000000}}""")) { Refuse = "app/version" })
            .GetSnapshotAsync(Client("qbittorrent"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Transmission_reads_its_version_from_session_get_without_the_build_hash()
    {
        var handler = new RoutedHandler(
            ("\"torrent-get\"", """{"result":"success","arguments":{"torrents":[]}}"""),
            ("\"session-get\"", """{"result":"success","arguments":{"download-dir":"/downloads","rpc-version":18,"version":"4.0.5 (a6fe2de319)"}}"""),
            ("\"free-space\"", """{"result":"success","arguments":{"path":"/downloads","size-bytes":15000000000}}"""));

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("4.0.5", snapshot!.ClientVersion);
        Assert.Equal(15_000_000_000L, snapshot.FreeSpaceBytes);
    }

    [Fact]
    public async Task Transmission_with_no_version_in_the_answer_says_nothing()
    {
        var handler = new RoutedHandler(
            ("\"torrent-get\"", """{"result":"success","arguments":{"torrents":[]}}"""),
            ("\"session-get\"", """{"result":"success","arguments":{"download-dir":"/downloads"}}"""),
            ("\"free-space\"", """{"result":"success","arguments":{"size-bytes":1500000000000}}"""));

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
    }

    [Fact]
    public async Task Deluge_reads_its_version_from_daemon_info()
    {
        var handler = new RoutedHandler(
            ("web.update_ui", """{"result":{"torrents":{}},"error":null,"id":2}"""),
            ("core.get_free_space", """{"result":8000000000,"error":null,"id":8}"""),
            ("daemon.info", """{"result":"2.1.1","error":null,"id":9}"""));

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("2.1.1", snapshot!.ClientVersion);
    }

    [Fact]
    public async Task Deluge_falls_back_to_get_version_for_an_older_daemon()
    {
        var handler = new RoutedHandler(
            ("web.update_ui", """{"result":{"torrents":{}},"error":null,"id":2}"""),
            ("core.get_free_space", """{"result":8000000000,"error":null,"id":8}"""),
            ("daemon.info", """{"result":null,"error":{"message":"Unknown method","code":2},"id":9}"""),
            ("daemon.get_version", """{"result":"1.3.15","error":null,"id":9}"""));

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("1.3.15", snapshot!.ClientVersion);
    }

    [Fact]
    public async Task Deluge_that_knows_neither_call_says_nothing()
    {
        var handler = new RoutedHandler(
            ("web.update_ui", """{"result":{"torrents":{}},"error":null,"id":2}"""),
            ("core.get_free_space", """{"result":8000000000,"error":null,"id":8}"""),
            ("daemon.info", """{"result":null,"error":{"message":"Unknown method","code":2},"id":9}"""),
            ("daemon.get_version", """{"result":null,"error":{"message":"Unknown method","code":2},"id":9}"""));

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
        Assert.Equal(8_000_000_000L, snapshot.FreeSpaceBytes);
    }

    [Fact]
    public async Task Utorrent_has_no_version_to_show()
    {
        var snapshot = await new UTorrentDownloadClient(() => new RoutedHandler(
                ("list=1", RecordedReplies.Utorrent("idle").Single.Reply.Body!),
                ("token.html", RecordedReplies.Utorrent("token").Single.Reply.Body!.Replace("<scrubbed>", "recorded-token"))))
            .GetSnapshotAsync(Client("utorrent"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
    }

    private static DownloadClientItem Client(string protocol)
        => new(
            Id: $"{protocol}-1", Name: protocol, Protocol: protocol, Host: "localhost", Port: 8080,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8080/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers the first route whose text is in the address or the request body; a request containing <see cref="Refuse"/> is answered 403; anything else is a sign-in succeeding.</summary>
    private sealed class RoutedHandler(params (string Match, string Reply)[] routes) : HttpMessageHandler
    {
        public string? Refuse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = (request.RequestUri?.ToString() ?? string.Empty) + (request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Refuse is not null && text.Contains(Refuse, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") };
            }

            foreach (var route in routes)
            {
                if (text.Contains(route.Match, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route.Reply, Encoding.UTF8, "application/json") };
                }
            }

            var login = text.Contains("auth/login", StringComparison.Ordinal) ? "Ok." : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(login, Encoding.UTF8, "application/json") };
        }
    }
}
