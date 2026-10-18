using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

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
        => AssertFinishedAndQuiet(await RecordedDelugeSnapshot("Seeding", 100.0, "Error: Connection refused"));

    [Fact]
    public async Task Deluge_paused_complete_torrent_with_a_tracker_error_is_not_failed()
    {
        var item = await RecordedDelugeSnapshot("Paused", 100.0, "Error: Connection refused");
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.Failure);
        Assert.NotEqual(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task Deluge_incomplete_torrent_with_a_tracker_error_keeps_the_error()
    {
        var item = await RecordedDelugeSnapshot("Downloading", 40.0, "Error: Connection refused");
        Assert.NotNull(item.ErrorMessage);
        Assert.NotNull(item.Failure);
    }

    /// <summary>The reply exactly as Deluge 2.2.0 sent it for a complete torrent whose tracker does not answer.</summary>
    [Fact]
    public async Task Deluge_complete_torrent_exactly_as_recorded_with_its_tracker_down_is_finished_and_quiet()
    {
        var handler = RecordedDelugeRig.Handler(RecordedReplies.Deluge("finished-tracker-error").Body("web.update_ui"));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Item("deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None);

        AssertFinishedAndQuiet(Assert.Single(snapshot!.Queue));
    }

    /// <summary>The reply exactly as Deluge 2.2.0 sent it for a torrent with no peers whose tracker does not exist: nothing ever arrives, and it keeps the error.</summary>
    [Fact]
    public async Task Deluge_torrent_that_never_started_exactly_as_recorded_keeps_the_tracker_error()
    {
        var handler = RecordedDelugeRig.Handler(RecordedReplies.Deluge("stalled").Body("web.update_ui"));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Item("deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.Equal(0, item.Progress);
        Assert.NotNull(item.ErrorMessage);
        Assert.NotNull(item.Failure);
    }

    [Fact]
    public async Task Transmission_complete_torrent_with_a_tracker_error_is_finished_and_quiet()
        => AssertFinishedAndQuiet(await RecordedTransmissionSnapshot(status: 6, percentDone: 1.0, error: 2, errorString: "Could not connect to tracker"));

    /// <summary>The reply exactly as Transmission 4.1.3 sent it for a complete torrent whose tracker is down: the failure is in the tracker report only.</summary>
    [Fact]
    public async Task Transmission_complete_torrent_exactly_as_recorded_with_its_tracker_down_is_finished_and_quiet()
    {
        var client = new TransmissionDownloadClient(new Factory(RecordedReplies.Handler("transmission", [RecordedReplies.Transmission("settings"), RecordedReplies.Transmission("finished-tracker-error")])));
        var snapshot = await client.GetSnapshotAsync(Item("transmission", 9091), DateTimeOffset.UtcNow, CancellationToken.None);

        AssertFinishedAndQuiet(Assert.Single(snapshot!.Queue));
    }

    [Fact]
    public async Task Transmission_complete_torrent_with_a_tracker_warning_is_finished_and_quiet()
        => AssertFinishedAndQuiet(await RecordedTransmissionSnapshot(status: 6, percentDone: 1.0, error: 1, errorString: "Tracker gave a warning"));

    [Fact]
    public async Task Transmission_incomplete_torrent_with_a_tracker_error_keeps_the_error()
    {
        var item = await RecordedTransmissionSnapshot(status: 4, percentDone: 0.4, error: 2, errorString: "Could not connect to tracker");
        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.Equal("Could not connect to tracker", item.ErrorMessage);
    }

    [Fact]
    public async Task Transmission_complete_torrent_with_a_local_error_is_still_an_error()
    {
        var item = await RecordedTransmissionSnapshot(status: 6, percentDone: 1.0, error: 3, errorString: "No space left on device");
        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.True(item.IsSetupProblem);
    }

    [Theory]
    [InlineData("stalledUP")]
    [InlineData("uploading")]
    [InlineData("pausedUP")]
    public async Task Qbittorrent_complete_torrent_whose_tracker_is_not_working_is_finished_and_quiet(string state)
    {
        var item = await RecordedQbitSnapshot(state, 1.0, "The tracker is not answering");
        AssertFinishedAndQuiet(item);
        Assert.NotEqual(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task Qbittorrent_incomplete_torrent_whose_tracker_is_not_working_keeps_its_behaviour()
    {
        var item = await RecordedQbitSnapshot("stalledDL", 0.4, "The tracker is not answering");
        // #1193: stalledDL alone stays Downloading until Deluno's no-progress window finds no byte gain.
        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    /// <summary>Deluge 2.2.0's recorded list of a finished torrent, in the state, progress and tracker status under test.</summary>
    private static async Task<DownloadQueueItem> RecordedDelugeSnapshot(string state, double progress, string trackerStatus)
    {
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Finished(
            ("state", state), ("progress", progress), ("label", "movies"), ("tracker_status", trackerStatus)));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Item("deluge", 8112), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    /// <summary>
    /// Transmission 4.1.3's recorded list of a complete torrent whose tracker does not answer (its own tracker report, "Could not
    /// connect to tracker", is in the reply), with the torrent's status, progress and error fields as under test.
    /// </summary>
    private static async Task<DownloadQueueItem> RecordedTransmissionSnapshot(int status, double percentDone, int error, string errorString)
    {
        var json = RecordedTransmissionRig.Reply(
            RecordedTransmissionRig.FinishedTrackerError,
            ("status", status), ("percentDone", percentDone), ("error", error), ("errorString", errorString));
        var client = new TransmissionDownloadClient(new Factory(RecordedTransmissionRig.Handler(json)));
        var snapshot = await client.GetSnapshotAsync(Item("transmission", 9091), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    /// <summary>
    /// qBittorrent 5.2.1's recorded list of a complete torrent whose tracker does not answer, in the state and progress under test.
    /// The tracker list is qBittorrent 5.2.4's recorded list for a tracker that is not working, with the tracker's reason changed to the one under test.
    /// </summary>
    private static async Task<DownloadQueueItem> RecordedQbitSnapshot(string state, double progress, string trackerMessage)
    {
        var info = RecordedQbittorrentRig.Info(RecordedQbittorrentRig.FinishedTrackerError, ("state", state), ("progress", progress), ("category", "movies"));
        var trackers = RecordedQbittorrentRig.Trackers(4, trackerMessage, "http://127.0.0.1:9/announce");
        var handler = RecordedQbittorrentRig.Handler(info, trackers);
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
}
