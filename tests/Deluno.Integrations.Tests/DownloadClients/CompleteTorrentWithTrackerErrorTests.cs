using System.Globalization;
using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1183: the tracker matters only while a torrent is still downloading. A
/// torrent at 100% whose tracker does not answer is finished: it must reach
/// the processor or the import as normal, never read as Stalled or failed.
/// An incomplete torrent with the same tracker error keeps its behaviour.
/// </summary>
public sealed class CompleteTorrentWithTrackerErrorTests
{
    private static void AssertFinishedAndQuiet(DownloadQueueItem item)
    {
        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.Failure);
    }

    [Fact]
    public async Task Deluge_seeding_torrent_with_a_tracker_error_is_finished_and_quiet()
        => AssertFinishedAndQuiet(await Deluge("Seeding", 100.0, "Error: Connection refused"));

    [Fact]
    public async Task Deluge_paused_complete_torrent_with_a_tracker_error_is_not_failed()
    {
        var item = await Deluge("Paused", 100.0, "Error: Connection refused");
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.Failure);
        Assert.NotEqual(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task Deluge_incomplete_torrent_with_a_tracker_error_keeps_the_error()
    {
        var item = await Deluge("Downloading", 40.0, "Error: Connection refused");
        Assert.NotNull(item.ErrorMessage);
        Assert.NotNull(item.Failure);
    }

    [Fact]
    public async Task Transmission_complete_torrent_with_a_tracker_error_is_finished_and_quiet()
        => AssertFinishedAndQuiet(await Transmission(status: 6, percentDone: 1.0, error: 2, errorString: "Could not connect to tracker"));

    [Fact]
    public async Task Transmission_complete_torrent_with_a_tracker_warning_is_finished_and_quiet()
        => AssertFinishedAndQuiet(await Transmission(status: 6, percentDone: 1.0, error: 1, errorString: "Tracker gave a warning"));

    [Fact]
    public async Task Transmission_incomplete_torrent_with_a_tracker_error_keeps_the_error()
    {
        var item = await Transmission(status: 4, percentDone: 0.4, error: 2, errorString: "Could not connect to tracker");
        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.Equal("Could not connect to tracker", item.ErrorMessage);
    }

    [Fact]
    public async Task Transmission_complete_torrent_with_a_local_error_is_still_an_error()
    {
        var item = await Transmission(status: 6, percentDone: 1.0, error: 3, errorString: "No space left on device");
        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.True(item.IsSetupProblem);
    }

    [Theory]
    [InlineData("stalledUP")]
    [InlineData("uploading")]
    [InlineData("pausedUP")]
    public async Task Qbittorrent_complete_torrent_whose_tracker_is_not_working_is_finished_and_quiet(string state)
    {
        var item = await Qbit(state, 1.0, "[{\"status\":4,\"msg\":\"The tracker is not answering\"}]");
        AssertFinishedAndQuiet(item);
        Assert.NotEqual(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task Qbittorrent_incomplete_torrent_whose_tracker_is_not_working_keeps_its_behaviour()
    {
        var item = await Qbit("stalledDL", 0.4, "[{\"status\":4,\"msg\":\"The tracker is not answering\"}]");
        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    private static async Task<DownloadQueueItem> Deluge(string state, double progress, string trackerStatus)
    {
        var torrent = "{\"result\":{\"torrents\":{\"abc123\":{\"name\":\"Sherlock.Jr.1924.1080p.BluRay\",\"state\":\"" + state
            + "\",\"progress\":" + progress.ToString(CultureInfo.InvariantCulture)
            + ",\"download_payload_rate\":0,\"upload_payload_rate\":0,\"eta\":0,\"total_size\":5000000000,\"total_done\":5000000000,\"num_peers\":0,"
            + "\"time_added\":1727000000,\"label\":\"movies\",\"message\":\"OK\",\"save_path\":\"/downloads\",\"tracker_status\":\""
            + trackerStatus + "\"}}},\"error\":null,\"id\":2}";
        var client = new DelugeDownloadClient(new Factory(new DelugeHandler(torrent)));
        var snapshot = await client.GetSnapshotAsync(Item("deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static async Task<DownloadQueueItem> Transmission(int status, double percentDone, int error, string errorString)
    {
        var json = "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":7,\"name\":\"Sherlock.Jr.1924.1080p.BluRay\","
            + $"\"status\":{status},\"percentDone\":{percentDone.ToString(CultureInfo.InvariantCulture)},"
            + "\"totalSize\":5000000000,\"downloadedEver\":5000000000,\"peersConnected\":0,\"peersSendingToUs\":0,"
            + $"\"addedDate\":{DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()},\"rateDownload\":0,\"rateUpload\":0,\"eta\":0,"
            + $"\"isStalled\":false,\"error\":{error},\"errorString\":\"{errorString}\"}}]}}}}";
        var client = new TransmissionDownloadClient(new Factory(new FixedHandler(json)));
        var snapshot = await client.GetSnapshotAsync(Item("transmission", 9091), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static async Task<DownloadQueueItem> Qbit(string state, double progress, string trackersJson)
    {
        var info = "[{\"hash\":\"abc123\",\"name\":\"Sherlock.Jr.1924.1080p.BluRay\",\"category\":\"movies\","
            + $"\"state\":\"{state}\",\"progress\":{progress.ToString(CultureInfo.InvariantCulture)},"
            + "\"dlspeed\":0,\"upspeed\":0,\"eta\":0,\"size\":5000000000,\"downloaded\":5000000000,\"num_seeds\":0,\"num_leechs\":0,"
            + $"\"added_on\":{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()},\"save_path\":\"/downloads\",\"content_path\":\"/downloads/item\"}}]";
        var handler = new QbitHandler(info, trackersJson);
        var snapshot = await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Item("qbittorrent", 8080), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Item(string type, int port)
        => new($"{type}-1", type, type, "localhost", port, null, null, $"http://localhost:{port}/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelugeHandler(string updateUi) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("web.update_ui", StringComparison.Ordinal) ? updateUi : "{\"result\":true,\"error\":null,\"id\":1}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class QbitHandler(string torrentsInfo, string trackersJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var reply = path.Contains("auth/login", StringComparison.Ordinal) ? "Ok."
                : path.Contains("torrents/trackers", StringComparison.Ordinal) ? trackersJson
                : path.Contains("torrents/info", StringComparison.Ordinal) ? torrentsInfo : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }
}
