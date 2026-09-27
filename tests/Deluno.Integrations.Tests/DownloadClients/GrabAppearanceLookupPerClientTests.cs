using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #911 (cleanup sheet B4), against each real adapter's own recorded response
/// shape rather than a mock <see cref="IDownloadClient"/>: the appearance
/// lookup reads whatever hash/id and name match the client's live queue for
/// the torrent clients (their own duplicate check runs at grab time, so a
/// second lookup never finds Duplicate/Rejected there — this is the "where
/// the client can report it" the issue draws), and reads the client's native
/// history for the two Usenet clients, which do carry a duplicate/rejected
/// outcome after the fact.
/// </summary>
public sealed class GrabAppearanceLookupPerClientTests
{
    private const string ClientId = "client-1";

    // ── qBittorrent ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Qbittorrent_found_under_its_own_category_is_reported_as_expected()
    {
        var torrentsInfo = QbittorrentTorrent("Example.Movie.2024.1080p", "abc123", "movies");
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => new QbittorrentHandler(torrentsInfo)));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
        Assert.Equal("abc123", result.QueueItemId);
    }

    [Fact]
    public async Task Qbittorrent_nothing_in_the_queue_is_reported_as_not_there()
    {
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => new QbittorrentHandler("[]")));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Qbittorrent_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => new FailingHandler()));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string QbittorrentTorrent(string name, string hash, string category)
        => "[{\"hash\":\"" + hash + "\",\"name\":\"" + name + "\",\"category\":\"" + category + "\","
            + "\"state\":\"downloading\",\"progress\":0.4,\"dlspeed\":0,\"upspeed\":0,\"eta\":0,\"size\":5000000000,\"downloaded\":2000000000,"
            + "\"num_seeds\":0,\"num_leechs\":0,"
            + $"\"added_on\":{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()},\"save_path\":\"/downloads\",\"content_path\":\"/downloads/item\"}}]";

    private sealed class QbittorrentHandler(string torrentsInfo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("auth/login", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.", Encoding.UTF8, "text/plain") });
            }

            if (path.Contains("torrents/trackers", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            }

            var reply = path.Contains("torrents/info", StringComparison.Ordinal) ? torrentsInfo : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }

    // ── Transmission ────────────────────────────────────────────────────────

    [Fact]
    public async Task Transmission_found_under_its_own_category_is_reported_as_expected()
    {
        var torrentGet = TransmissionTorrent("Example.Movie.2024.1080p", 7, "movies");
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(new TransmissionHandler(torrentGet))));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "7", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
    }

    [Fact]
    public async Task Transmission_nothing_in_the_queue_is_reported_as_not_there()
    {
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(new TransmissionHandler("""{"result":"success","arguments":{"torrents":[]}}"""))));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "7", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Transmission_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(new FailingHandler())));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "7", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string TransmissionTorrent(string name, int id, string category)
    {
        var addedDate = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        return "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":" + id + ",\"name\":\"" + name + "\",\"labels\":[\"" + category + "\"],"
            + "\"status\":4,\"percentDone\":0.4,\"totalSize\":5000000000,\"downloadedEver\":2000000000,\"peersConnected\":1,\"peersSendingToUs\":1,"
            + "\"addedDate\":" + addedDate + ",\"rateDownload\":0,\"rateUpload\":0,\"eta\":0,\"isStalled\":false,\"error\":0}]}}";
    }

    private sealed class TransmissionHandler(string torrentGet) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("\"torrent-get\"", StringComparison.Ordinal) ? torrentGet : "{\"result\":\"success\",\"arguments\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    // ── Deluge ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deluge_found_under_its_own_category_is_reported_as_expected()
    {
        var torrent = DelugeTorrent("Example.Movie.2024.1080p", "def456", "movies");
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(new DelugeHandler(torrent))));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
    }

    [Fact]
    public async Task Deluge_nothing_in_the_queue_is_reported_as_not_there()
    {
        var empty = """{"result":{"torrents":{}},"error":null,"id":2}""";
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(new DelugeHandler(empty))));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Deluge_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(new FailingHandler())));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string DelugeTorrent(string name, string hash, string category)
        => "{\"result\":{\"torrents\":{\"" + hash + "\":{\"name\":\"" + name + "\",\"state\":\"Downloading\","
            + "\"progress\":40.0,\"download_payload_rate\":0,\"upload_payload_rate\":0,\"eta\":0,\"total_size\":5000000000,\"total_done\":2000000000,\"num_peers\":2,"
            + "\"time_added\":1727000000,\"label\":\"" + category + "\",\"message\":\"OK\",\"save_path\":\"/downloads\",\"tracker_status\":\"Announce sent\"}}},\"error\":null,\"id\":2}";

    private sealed class DelugeHandler(string updateUi) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("web.update_ui", StringComparison.Ordinal) ? updateUi : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    // ── uTorrent ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UTorrent_found_under_its_own_category_is_reported_as_expected()
    {
        var hash = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";
        var list = UTorrentList(hash, "Example.Movie.2024.1080p", "movies");
        var registry = RegistryFor("utorrent", new UTorrentDownloadClient(() => new UTorrentHandler(list, """{"props":[]}""")));

        var result = await new GrabAppearanceLookupService(Connections("utorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", hash, "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
    }

    [Fact]
    public async Task UTorrent_nothing_in_the_queue_is_reported_as_not_there()
    {
        var registry = RegistryFor("utorrent", new UTorrentDownloadClient(() => new UTorrentHandler("""{"torrents":[]}""", """{"props":[]}""")));

        var result = await new GrabAppearanceLookupService(Connections("utorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task UTorrent_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("utorrent", new UTorrentDownloadClient(() => new FailingHandler()));

        var result = await new GrabAppearanceLookupService(Connections("utorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string UTorrentList(string hash, string name, string label)
        // uTorrent's list array is positional -- see UTorrentSnapshotTests for
        // the full field map. Index 11 is the label Deluno reads as category.
        => $$"""
        {"torrents":[[
            "{{hash}}", 1, "{{name}}", 1000000, 400,
            0, 0, 0, 1000000, 500000, 60, "{{label}}", 3,
            0, 0, 0, 0, 0, 0, 0, 0,
            "", 0, 1700000000, 0, 0, "D:\\Downloads\\release"
        ]]}
        """;

    private sealed class UTorrentHandler(string listResponse, string getPropsResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            var reply = uri.Contains("token.html", StringComparison.Ordinal)
                ? "<div id='token'>test-token</div>"
                : uri.Contains("action=getprops", StringComparison.Ordinal)
                    ? getPropsResponse
                    : uri.Contains("list=1", StringComparison.Ordinal)
                        ? listResponse
                        : "{}";
            var contentType = uri.Contains("token.html", StringComparison.Ordinal) ? "text/html" : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, contentType) });
        }
    }

    // ── SABnzbd ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sabnzbd_a_duplicate_the_client_already_held_is_reported_as_a_duplicate()
    {
        var history = SabnzbdHistory("nzo-1", "Example.Release.2026", "movies", status: "Failed", failMessage: "Duplicate NZB");
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(new SabnzbdHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-1", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_a_release_it_rejected_is_reported_as_rejected()
    {
        var history = SabnzbdHistory("nzo-2", "Example.Release.2026", "movies", status: "Failed", failMessage: "Unpacking failed, archive files are missing");
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(new SabnzbdHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-2", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_nothing_anywhere_is_reported_as_not_there()
    {
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(new SabnzbdHandler("""{"history":{"slots":[]}}"""))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-3", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(new FailingHandler())));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-4", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string SabnzbdHistory(string nzoId, string name, string category, string status, string failMessage)
        => "{\"history\":{\"slots\":[{\"nzo_id\":\"" + nzoId + "\",\"name\":\"" + name + "\",\"category\":\"" + category
            + "\",\"status\":\"" + status + "\",\"fail_message\":\"" + failMessage + "\",\"bytes\":\"0\",\"completed\":0,\"storage\":\"\"}]}}";

    private sealed class SabnzbdHandler(string historyJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            var reply = uri.Contains("mode=history", StringComparison.Ordinal) ? historyJson : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }

    // ── NZBGet ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nzbget_a_duplicate_the_client_already_held_is_reported_as_a_duplicate()
    {
        var history = NzbGetHistory(nzbId: 42, name: "Example.Release.2026", category: "movies", status: "DELETED/DUPE");
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(new NzbGetHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "42", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_a_release_it_rejected_is_reported_as_rejected()
    {
        var history = NzbGetHistory(nzbId: 43, name: "Example.Release.2026", category: "movies", status: "FAILURE/PAR");
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(new NzbGetHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "43", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_nothing_anywhere_is_reported_as_not_there()
    {
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(new NzbGetHandler("""{"version":"1.1","result":[]}"""))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "44", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(new FailingHandler())));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "45", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    private static string NzbGetHistory(int nzbId, string name, string category, string status)
        => "{\"version\":\"1.1\",\"result\":[{\"NZBID\":" + nzbId + ",\"NZBName\":\"" + name + "\",\"Category\":\"" + category
            + "\",\"Status\":\"" + status + "\",\"FileSizeHi\":0,\"FileSizeLo\":0,\"HistoryTime\":1700000000,\"DestDir\":\"\",\"ScriptStatuses\":[]}]}";

    private sealed class NzbGetHandler(string historyReply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("\"history\"", StringComparison.Ordinal) ? historyReply
                : body.Contains("\"listgroups\"", StringComparison.Ordinal) ? """{"version":"1.1","result":[]}"""
                : body.Contains("\"status\"", StringComparison.Ordinal) ? """{"version":"1.1","result":{}}"""
                : body.Contains("\"version\"", StringComparison.Ordinal) ? """{"version":"1.1","result":"21.0"}"""
                : """{"version":"1.1","result":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    // ── shared fixtures ──────────────────────────────────────────────────────

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IDownloadClientRegistry RegistryFor(string protocol, IDownloadClient client)
    {
        var registry = new Mock<IDownloadClientRegistry>();
        registry.Setup(instance => instance.TryGet(protocol, out client)).Returns(true);
        return registry.Object;
    }

    private static IConnectionsRepository Connections(string protocol)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Client(protocol)]);
        return repository.Object;
    }

    private static DownloadClientItem Client(string protocol)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            ClientId, "Test Client", protocol, "localhost", 8080, "user", "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
    }
}
