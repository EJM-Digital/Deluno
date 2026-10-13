using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #661 — Transmission never requested <c>uploadRatio</c>/<c>secondsSeeding</c>,
/// so <see cref="DownloadQueueItem.Ratio"/>/<see cref="DownloadQueueItem.SeedingMinutes"/>
/// stayed null forever and a sharing rule could never be judged met. Its own
/// <c>isStalled</c> flag was never read either, so Deluno waited on a cruder,
/// generic no-throughput check instead of Transmission's own, better-informed
/// one. The peer count used every connected peer rather than
/// <c>peersSendingToUs</c>, and a person-stopped torrent (status 0) read
/// identically to one merely queued behind others.
/// </summary>
public sealed class TransmissionSharingAndStallTests
{
    [Fact]
    public async Task Ratio_and_seeding_minutes_are_read_from_the_torrent_get_response()
    {
        var item = await Snapshot(status: 6, percentDone: 1, uploadRatio: 2.5, secondsSeeding: 7200, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(2.5, item.Ratio);
        Assert.Equal(120, item.SeedingMinutes);
    }

    [Fact]
    public async Task Seeding_minutes_is_null_when_transmission_never_reports_it()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 2, isStalled: false);

        Assert.Null(item.Ratio);
        Assert.Null(item.SeedingMinutes);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.99995)]
    public async Task Transmissions_own_isStalled_flag_alone_is_not_read_as_stalled(double percentDone)
    {
        // #1193: Deluno's own no-progress window decides, at any percent (DownloadClientHelpers.ReconcileTorrentStatus).
        var item = await Snapshot(status: 4, percentDone: percentDone, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: true);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task IsStalled_false_leaves_an_actively_downloading_torrent_alone()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task IsStalled_does_not_override_an_error_or_a_completed_download()
    {
        var completed = await Snapshot(status: 6, percentDone: 1, uploadRatio: 1, secondsSeeding: 60, peersSendingToUs: 0, isStalled: true);

        Assert.Equal(DownloadQueueStatuses.ImportReady, completed.Status);
    }

    /// <summary>
    /// Peers still reflects connected peers, not merely ones sending payload
    /// data: a magnet exchanging metadata over a connected peer has sent no
    /// piece data yet, and switching Peers to peersSendingToUs would make
    /// ReconcileTorrentStatus's no-metadata-no-peers window read that as no
    /// connectivity at all and stall it on the first poll.
    /// </summary>
    [Fact]
    public async Task Peers_reflects_connected_peers_so_a_metadata_only_magnet_is_not_read_as_peerless()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false, peersConnected: 5);

        Assert.Equal(5, item.Peers);
    }

    [Fact]
    public async Task Status_zero_is_paused_by_a_person_not_queued()
    {
        var item = await Snapshot(status: 0, percentDone: 0.2, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Paused, item.Status);
    }

    [Fact]
    public async Task Status_one_still_reads_as_queued()
    {
        var item = await Snapshot(status: 1, percentDone: 0, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    /// <summary>
    /// #912: a torrent that finished long ago and is now being re-verified
    /// (TR_STATUS_CHECK) still reports percentDone at 1 throughout the check.
    /// That must never read as ImportReady — Transmission could still rewrite
    /// the file underneath an import that raced ahead of the check.
    /// </summary>
    [Fact]
    public async Task Status_two_never_reads_as_import_ready_even_at_full_progress()
    {
        var item = await Snapshot(status: 2, percentDone: 1, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.NotTransferring, item.Status);
    }

    /// <summary>
    /// #880/#881: Transmission's own error field distinguishes a local
    /// problem (3, TR_STAT_LOCAL_ERROR — disk space, permissions) from the
    /// tracker complaining about the release (1 warning, 2 error). Only the
    /// local one is a setup problem.
    /// </summary>
    [Theory]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public async Task IsSetupProblem_reflects_only_transmissions_local_error_code(int errorCode, bool expected)
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: false, error: errorCode, errorString: errorCode == 0 ? null : "Something went wrong");

        Assert.Equal(expected, item.IsSetupProblem);
    }

    /// <summary>#973: activityDate/doneDate feed LastActivityUtc — whichever is more recent wins.</summary>
    [Fact]
    public async Task Last_activity_is_read_from_activity_date_when_it_is_the_most_recent()
    {
        var activity = DateTimeOffset.UtcNow.AddMinutes(-2);
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: false,
            activityDate: activity.ToUnixTimeSeconds(), doneDate: 0);

        Assert.Equal(activity.ToUnixTimeSeconds(), item.LastActivityUtc!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Last_activity_falls_back_to_done_date_when_it_is_the_most_recent()
    {
        var done = DateTimeOffset.UtcNow.AddMinutes(-1);
        var stale = DateTimeOffset.UtcNow.AddHours(-3);
        var item = await Snapshot(status: 6, percentDone: 1, uploadRatio: 1, secondsSeeding: 60, peersSendingToUs: 0, isStalled: false,
            activityDate: stale.ToUnixTimeSeconds(), doneDate: done.ToUnixTimeSeconds());

        Assert.Equal(done.ToUnixTimeSeconds(), item.LastActivityUtc!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Last_activity_is_null_when_transmission_reports_neither_field()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: false,
            activityDate: 0, doneDate: 0);

        Assert.Null(item.LastActivityUtc);
    }

    private static async Task<DownloadQueueItem> Snapshot(
        int status,
        double percentDone,
        double? uploadRatio,
        long? secondsSeeding,
        int peersSendingToUs,
        bool isStalled,
        int peersConnected = 0,
        int error = 0,
        string? errorString = null,
        long activityDate = 0,
        long doneDate = 0)
    {
        // Transmission 4.1.3's recorded list of a download in progress, in the status, progress, swarm, error and sharing values under
        // test. The recording's field list was shorter than the one Deluno asks for now, so the sharing and activity figures are added to
        // the recorded torrent; a figure Transmission does not send is taken out.
        var edits = new List<(string, object?)>
        {
            ("status", status), ("percentDone", percentDone), ("peersConnected", peersConnected), ("peersSendingToUs", peersSendingToUs),
            ("addedDate", DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()), ("activityDate", activityDate), ("doneDate", doneDate),
            ("rateDownload", 0), ("rateUpload", 0), ("eta", 0), ("isStalled", isStalled), ("error", error),
            ("errorString", errorString ?? string.Empty), ("uploadRatio", uploadRatio), ("secondsSeeding", secondsSeeding)
        };
        var torrentGet = RecordedTransmissionRig.Reply(RecordedTransmissionRig.Downloading, edits.ToArray());
        var handler = RecordedTransmissionRig.Handler(torrentGet);
        var client = new TransmissionDownloadClient(new Factory(handler));
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
