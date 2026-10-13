using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
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
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => RecordedQbittorrentRig.Handler(torrentsInfo)));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
        Assert.Equal("abc123", result.QueueItemId);
    }

    [Fact]
    public async Task Qbittorrent_nothing_in_the_queue_is_reported_as_not_there()
    {
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => RecordedQbittorrentRig.Handler(RecordedReplies.Qbittorrent("idle").Body("torrents/info"))));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Qbittorrent_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("qbittorrent", new QbittorrentDownloadClient(() => RecordedReplies.Qbittorrent("not-answering").NotAnswering()));

        var result = await new GrabAppearanceLookupService(Connections("qbittorrent"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "abc123", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>qBittorrent 5.2.1's recorded list of a download in progress, as the torrent under test.</summary>
    private static string QbittorrentTorrent(string name, string hash, string category)
        => RecordedQbittorrentRig.Info(RecordedQbittorrentRig.Downloading, ("name", name), ("hash", hash), ("category", category));

    // ── Transmission ────────────────────────────────────────────────────────

    // Transmission keys a torrent on its hash.
    private const string TransmissionHash = "ff7447384425ba526a3efb9efb792f7ba67a77dd";

    [Fact]
    public async Task Transmission_found_under_its_own_category_is_reported_as_expected()
    {
        var torrentGet = TransmissionTorrent("Example.Movie.2024.1080p", TransmissionHash, "movies");
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(RecordedTransmissionRig.Handler(torrentGet))));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", TransmissionHash, "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
    }

    [Fact]
    public async Task Transmission_nothing_in_the_queue_is_reported_as_not_there()
    {
        var empty = RecordedReplies.Transmission("idle").Body("torrent-get", ("^arguments.torrents", 0));
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(RecordedTransmissionRig.Handler(empty))));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "7", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Transmission_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("transmission", new TransmissionDownloadClient(new Factory(RecordedReplies.Transmission("not-answering").NotAnswering())));

        var result = await new GrabAppearanceLookupService(Connections("transmission"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "7", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>Transmission 4.1.3's recorded list of a download in progress, as the torrent under test.</summary>
    private static string TransmissionTorrent(string name, string hash, string category)
        => RecordedTransmissionRig.Reply(RecordedTransmissionRig.Downloading, ("hashString", hash), ("name", name), ("labels", new System.Text.Json.Nodes.JsonArray(category)));

    // ── Deluge ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deluge_found_under_its_own_category_is_reported_as_expected()
    {
        var torrent = DelugeTorrent("Example.Movie.2024.1080p", "def456", "movies");
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(torrent))));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
    }

    [Fact]
    public async Task Deluge_nothing_in_the_queue_is_reported_as_not_there()
    {
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(RecordedReplies.Deluge("idle").Body("web.update_ui")))));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Deluge_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("deluge", new DelugeDownloadClient(new Factory(RecordedReplies.Deluge("not-answering").NotAnswering())));

        var result = await new GrabAppearanceLookupService(Connections("deluge"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "def456", "Example.Movie.2024.1080p", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>Deluge 2.2.0's recorded list of a download in progress, with its one torrent given the hash, name and label under test.</summary>
    private static string DelugeTorrent(string name, string hash, string category)
        => RecordedReplies.Deluge("downloading").Body(
            "web.update_ui",
            (">result.torrents.c8bd46949c0daee70a4abf5e3096df264b7ac60c", hash), ("result.torrents." + hash + ".name", name), ("result.torrents." + hash + ".label", category));

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
        // SABnzbd 5.1.3's recorded history of a job that failed, with the fail message of a duplicate (the wording of a refused duplicate is not
        // yet recorded; every other value is SABnzbd's own).
        var history = SabnzbdHistory("nzo-1", "Example.Release.2026", "movies", failMessage: "Duplicate NZB");
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(SabnzbdHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-1", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_a_release_it_rejected_is_reported_as_rejected()
    {
        // The recorded failed job exactly as SABnzbd words it: the address would not fetch.
        var history = SabnzbdHistory("nzo-2", "Example.Release.2026", "movies", failMessage: null);
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(SabnzbdHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-2", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_nothing_anywhere_is_reported_as_not_there()
    {
        // SABnzbd 5.1.3's recorded history, which holds a different job.
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(SabnzbdHandler(RecordedReplies.Sabnzbd("idle").Body("mode=history")))));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-3", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Sabnzbd_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("sabnzbd", new SabnzbdDownloadClient(new Factory(RecordedReplies.Sabnzbd("not-answering").NotAnswering())));

        var result = await new GrabAppearanceLookupService(Connections("sabnzbd"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "nzo-4", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>SABnzbd 5.1.3's recorded history of its one failed job, as the job under test.</summary>
    private static string SabnzbdHistory(string nzoId, string name, string category, string? failMessage)
    {
        var edits = new List<(string, object?)> { ("history.slots.0.nzo_id", nzoId), ("history.slots.0.name", name), ("history.slots.0.category", category) };
        if (failMessage is not null)
        {
            edits.Add(("history.slots.0.fail_message", failMessage));
        }

        return RecordedReplies.Sabnzbd("failed").Body("mode=history", edits.ToArray());
    }

    private static HttpMessageHandler SabnzbdHandler(string historyJson)
        => RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")], (request, _) =>
            request.RequestUri!.Query.Contains("mode=history", StringComparison.Ordinal) ? RecordedReplies.Json(historyJson) : null);

    // ── NZBGet ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nzbget_a_duplicate_the_client_already_held_is_reported_as_a_duplicate()
    {
        // NZBGet 26.3's recorded history row of a job it deleted as a duplicate (DELETED/DUPE), as the job under test.
        var history = NzbGetHistory(nzbId: 42, name: "Example.Release.2026", category: "movies", status: "DELETED/DUPE");
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(NzbGetHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "42", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_a_release_it_rejected_is_reported_as_rejected()
    {
        var history = NzbGetHistory(nzbId: 43, name: "Example.Release.2026", category: "movies", status: "FAILURE/PAR");
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(NzbGetHandler(history))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "43", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_nothing_anywhere_is_reported_as_not_there()
    {
        // NZBGet 26.3's recorded history, which holds other jobs.
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(NzbGetHandler(RecordedReplies.Nzbget("idle").Body("history\"")))));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "44", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Nzbget_that_cannot_be_reached_is_unreachable_not_bad()
    {
        var registry = RegistryFor("nzbget", new NzbGetDownloadClient(new Factory(RecordedReplies.Nzbget("not-answering").NotAnswering())));

        var result = await new GrabAppearanceLookupService(Connections("nzbget"), registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "45", "Example.Release.2026", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>NZBGet 26.3's recorded history, cut to its one deleted-duplicate row, as the job and status under test.</summary>
    private static string NzbGetHistory(int nzbId, string name, string category, string status)
        => RecordedReplies.Nzbget("idle").Body(
            "history\"",
            ("^result", 1), ("result.0.NZBID", nzbId), ("result.0.NZBName", name), ("result.0.Name", name), ("result.0.Category", category), ("result.0.Status", status));

    private static HttpMessageHandler NzbGetHandler(string historyReply)
        => RecordedReplies.Handler(
            "nzbget",
            [RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("settings"), RecordedReplies.Nzbget("version")],
            (_, body) => body.Contains("\"history\"", StringComparison.Ordinal) ? RecordedReplies.Json(historyReply)
                // The recorded settings say NZBGet is paused (a fresh one starts paused), so the client reads its log (#1257) for a
                // configuration error. The recorded log reply, with its lines taken out, is a log with no such error.
                : body.Contains("\"log\"", StringComparison.Ordinal) ? RecordedReplies.Json(RecordedReplies.Nzbget("config-error-paused").Body("log\"", ("^result", 0)))
                : null);

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
