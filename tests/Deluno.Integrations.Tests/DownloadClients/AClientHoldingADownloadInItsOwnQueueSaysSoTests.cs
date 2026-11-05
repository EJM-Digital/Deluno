using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1493: a download a client holds in its own queue (qBittorrent's queuedDL behind "Maximum active downloads", Transmission's status 3,
/// Deluge's Queued, uTorrent's queued bit, SABnzbd's and NZBGet's queued job) reads Queued with its place in that queue, and the client
/// says how many downloads it runs at once where it exposes that. Every reply is the client's own recording. Where a recording was of
/// another state of the same job, the one value the test is about is changed (and the test says which).
/// rTorrent has no queue of its own, so it has no case here.
/// </summary>
public sealed class AClientHoldingADownloadInItsOwnQueueSaysSoTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request, request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DownloadClientItem Client(string name, string protocol, int port)
        => new("client-1", name, protocol, "localhost", port, null, "api-key", $"http://localhost:{port}/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // ------------------------------------------------------------------ qBittorrent

    private static async Task<DownloadClientTelemetrySnapshot> QbittorrentAsync(string recording, params (string Path, object? Value)[] preferenceEdits)
    {
        var handler = RecordedQbittorrentRig.Handler(
            RecordedReplies.Qbittorrent(recording).Body("torrents/info"),
            intercept: (request, _) => preferenceEdits.Length > 0 && request.RequestUri!.AbsolutePath.EndsWith("app/preferences", StringComparison.Ordinal)
                ? Json(RecordedReplies.Qbittorrent("settings").Body("app/preferences", preferenceEdits))
                : null);
        return (await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qBittorrent", "qbittorrent", 8081), DateTimeOffset.UtcNow, CancellationToken.None))!;
    }

    [Fact]
    public async Task A_qBittorrent_torrent_held_by_its_active_download_limit_has_its_place_and_the_client_its_limit()
    {
        var snapshot = await QbittorrentAsync("queued");

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
        Assert.Equal(2, item.QueuePosition); // the recorded torrent's priority
        Assert.Equal(5, snapshot.ActiveDownloadLimit); // max_active_downloads in the recorded preferences
    }

    [Fact]
    public async Task A_qBittorrent_torrent_that_is_downloading_has_no_queue_place()
    {
        var snapshot = await QbittorrentAsync("downloading");

        Assert.All(snapshot.Queue, item => Assert.Null(item.QueuePosition));
    }

    [Theory]
    [InlineData("queueing_enabled", false)]
    [InlineData("max_active_downloads", -1)]
    public async Task qBittorrent_names_no_limit_when_queueing_is_off_or_the_limit_is_unlimited(string field, object value)
    {
        var snapshot = await QbittorrentAsync("queued", (field, value));

        Assert.Null(snapshot.ActiveDownloadLimit);
    }

    // ------------------------------------------------------------------ Transmission

    [Fact]
    public async Task A_Transmission_torrent_queued_to_download_has_its_place_and_the_client_its_queue_size()
    {
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply("queued"));
        var snapshot = (await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("Transmission", "transmission", 9091), DateTimeOffset.UtcNow, CancellationToken.None))!;

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal(DownloadQueueStatuses.Queued, item.Status); // status 3
        Assert.Equal(11, item.QueuePosition); // queuePosition 10 counts from 0
        Assert.Equal(5, snapshot.ActiveDownloadLimit); // download-queue-size, with download-queue-enabled true
    }

    // ------------------------------------------------------------------ Deluge

    [Fact]
    public async Task A_Deluge_torrent_in_its_queue_has_its_place()
    {
        var handler = RecordedDelugeRig.Handler(RecordedReplies.Deluge("queued").Body("web.update_ui"));
        var snapshot = (await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("Deluge", "deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None))!;

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
        Assert.Equal(2, item.QueuePosition); // queue 1 counts from 0
        // The recorded core settings do not carry max_active_downloading (see tests/fixtures/recorded/MISSING.md), so no limit is named.
        Assert.Null(snapshot.ActiveDownloadLimit);
    }

    [Fact]
    public async Task Deluge_names_its_active_download_limit_when_it_says_it()
    {
        // The recorded core.get_config_values reply (settings) with the one key asked for added; the reply's shape is the recorded one.
        var withLimit = RecordedReplies.Deluge("settings").Body("core.get_config_values", ("result.max_active_downloading", 3));
        var handler = RecordedDelugeRig.Handler(RecordedReplies.Deluge("queued").Body("web.update_ui"), coreConfig: withLimit);
        var snapshot = (await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("Deluge", "deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None))!;

        Assert.Equal(3, snapshot.ActiveDownloadLimit);
    }

    // ------------------------------------------------------------------ uTorrent

    [Fact]
    public async Task A_uTorrent_torrent_waiting_in_its_queue_has_its_place_and_the_client_its_limit()
    {
        var list = RecordedReplies.Utorrent("queued").Exchanges.Last(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!;
        var handler = new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)) return Json(RecordedReplies.UtorrentTokenPage());
            if (uri.Query.Contains("list=1", StringComparison.Ordinal)) return Json(list);
            if (uri.Query.Contains("action=getsettings", StringComparison.Ordinal)) return Json(RecordedReplies.Utorrent("settings").Single.Reply.Body!);
            return Json("{}");
        });
        var snapshot = (await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client("uTorrent", "utorrent", 8080), DateTimeOffset.UtcNow, CancellationToken.None))!;

        var waiting = Assert.Single(snapshot.Queue, item => item.Status == DownloadQueueStatuses.Queued);
        Assert.Equal(2, waiting.QueuePosition); // list index 17, queue order
        var running = Assert.Single(snapshot.Queue, item => item.Status != DownloadQueueStatuses.Queued);
        Assert.Null(running.QueuePosition);
        Assert.Equal(5, snapshot.ActiveDownloadLimit); // max_active_downloads in the recorded settings
    }

    // ------------------------------------------------------------------ SABnzbd and NZBGet

    [Fact]
    public async Task A_SABnzbd_job_waiting_in_a_running_queue_has_its_place()
    {
        // The recorded queued job sat in a paused queue; the same recorded slot with the queue running is only waiting (index 0 is first).
        var queue = RecordedReplies.Sabnzbd("queued").Body("mode=queue&output=json&apikey", ("queue.paused", false));
        var history = RecordedReplies.Sabnzbd("idle").Body("mode=history&output");
        var version = RecordedReplies.Sabnzbd("version").Body("mode=version");
        var handler = new Handler((request, _) =>
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            return Json(query.Contains("mode=history", StringComparison.Ordinal) ? history : query.Contains("mode=version", StringComparison.Ordinal) ? version : queue);
        });
        var snapshot = (await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("SABnzbd", "sabnzbd", 8080), DateTimeOffset.UtcNow, CancellationToken.None))!;

        var waiting = snapshot.Queue.Where(item => item.Status == DownloadQueueStatuses.Queued).ToArray();
        Assert.NotEmpty(waiting);
        Assert.All(waiting, item => Assert.NotNull(item.QueuePosition));
        Assert.Null(snapshot.ActiveDownloadLimit); // SABnzbd runs one job at a time and gives no number
    }

    [Fact]
    public async Task An_NZBGet_job_waiting_is_placed_by_its_order_in_NZBGets_list()
    {
        // NZBGet's recorded list holds a job it was fetching and a paused one; the second job's status is set to QUEUED, the one value this is about.
        var list = RecordedReplies.Nzbget("queued").Body("listgroups", ("result.1.Status", "QUEUED"));
        var handler = new Handler((_, body) => body.Contains("\"listgroups\"", StringComparison.Ordinal) ? Json(list)
            : body.Contains("\"history\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("removed-with-data").Response("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray()))
            : RecordedReplies.Nzbget("settings").Response("\"status\""));
        var snapshot = (await new NzbGetDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("NZBGet", "nzbget", 6789), DateTimeOffset.UtcNow, CancellationToken.None))!;

        var waiting = Assert.Single(snapshot.Queue, item => item.Status == DownloadQueueStatuses.Queued);
        Assert.Equal(2, waiting.QueuePosition);
        Assert.All(snapshot.Queue.Where(item => item.Status != DownloadQueueStatuses.Queued), item => Assert.Null(item.QueuePosition));
        Assert.Null(snapshot.ActiveDownloadLimit);
    }
}
