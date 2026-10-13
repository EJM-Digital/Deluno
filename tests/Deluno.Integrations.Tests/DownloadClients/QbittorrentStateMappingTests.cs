using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #660 — every state qBittorrent's <c>torrents/info</c> can report for
/// <c>state</c>, checked against the WebUI API's own enum. Before this,
/// <c>missingFiles</c>, the three checking states and <c>moving</c> all fell
/// through to the default arm and read as a healthy, active download; a
/// person-paused, incomplete torrent (<c>pausedDL</c>/<c>stoppedDL</c>) read
/// identically to one merely waiting behind others in the queue
/// (<c>queuedDL</c>).
/// </summary>
public sealed class QbittorrentStateMappingTests
{
    [Theory]
    [InlineData("downloading", DownloadQueueStatuses.Downloading)]
    [InlineData("metaDL", DownloadQueueStatuses.Downloading)]
    [InlineData("forcedDL", DownloadQueueStatuses.Downloading)]
    [InlineData("allocating", DownloadQueueStatuses.Downloading)]
    [InlineData("stalledDL", DownloadQueueStatuses.Downloading)]
    [InlineData("error", DownloadQueueStatuses.Stalled)]
    // The top-priority defect: the payload is gone from disk and this cannot
    // progress on its own, but used to fall through to Downloading.
    [InlineData("missingFiles", DownloadQueueStatuses.Stalled)]
    // Verifying and relocating: no bytes moving on the wire, so their own
    // status rather than Downloading, which let the no-throughput/excessive-
    // ETA checks (#659) reason about a torrent that cannot show either.
    [InlineData("checkingDL", DownloadQueueStatuses.NotTransferring)]
    [InlineData("checkingUP", DownloadQueueStatuses.NotTransferring)]
    [InlineData("checkingResumeData", DownloadQueueStatuses.NotTransferring)]
    [InlineData("moving", DownloadQueueStatuses.NotTransferring)]
    // A person paused this, incomplete, download - distinct from queuedDL,
    // which is qBittorrent holding it back on its own.
    [InlineData("pausedDL", DownloadQueueStatuses.Paused)]
    [InlineData("stoppedDL", DownloadQueueStatuses.Paused)]
    [InlineData("queuedDL", DownloadQueueStatuses.Queued)]
    [InlineData("uploading", DownloadQueueStatuses.ImportReady)]
    [InlineData("pausedUP", DownloadQueueStatuses.ImportReady)]
    [InlineData("stoppedUP", DownloadQueueStatuses.ImportReady)]
    [InlineData("queuedUP", DownloadQueueStatuses.ImportReady)]
    [InlineData("stalledUP", DownloadQueueStatuses.ImportReady)]
    [InlineData("forcedUP", DownloadQueueStatuses.ImportReady)]
    [InlineData("unknown", DownloadQueueStatuses.Downloading)]
    public async Task Every_qbittorrent_state_maps_to_the_right_status(string state, string expected)
    {
        // Completed states report progress 1; qBittorrent's own convention.
        var progress = state is "uploading" or "pausedUP" or "stoppedUP" or "queuedUP" or "stalledUP" or "forcedUP" ? 1.0 : 0.4;
        var item = await Snapshot(state, progress);

        Assert.Equal(expected, item.Status);
    }

    /// <summary>
    /// #912: qBittorrent reports progress 1.0 throughout a re-check of an
    /// already-complete torrent, and while it relocates a finished torrent's
    /// files (moving). Checked ahead of the progress-based ImportReady test,
    /// so a re-check or a move never races the import against a file
    /// qBittorrent still has open.
    /// </summary>
    [Theory]
    [InlineData("checkingDL")]
    [InlineData("checkingUP")]
    [InlineData("checkingResumeData")]
    [InlineData("moving")]
    public async Task Checking_or_moving_never_reads_as_import_ready_even_at_full_progress(string state)
    {
        var item = await Snapshot(state, progress: 1.0);

        Assert.Equal(DownloadQueueStatuses.NotTransferring, item.Status);
    }

    [Fact]
    public async Task MissingFiles_carries_a_plain_words_explanation()
    {
        var item = await Snapshot("missingFiles", 0.4);

        Assert.Equal("qBittorrent reports this torrent's data is missing from disk.", item.ErrorMessage);
    }

    /// <summary>#880/#881: missingFiles and a bare "error" state are this machine's problem, never the release's.</summary>
    [Theory]
    [InlineData("missingFiles", true)]
    [InlineData("error", true)]
    // A plain stalled state, with no error text, means no seeds or peers - a
    // question about the release, not a setup problem.
    [InlineData("stalledDL", false)]
    [InlineData("downloading", false)]
    public async Task IsSetupProblem_reflects_whether_qbittorrent_reported_a_local_error(string state, bool expected)
    {
        var item = await Snapshot(state, 0.4);

        Assert.Equal(expected, item.IsSetupProblem);
    }

    [Fact]
    public async Task Peers_is_seeds_plus_leechers()
    {
        var item = await Snapshot("downloading", 0.4, numSeeds: 2, numLeechs: 5);

        Assert.Equal(7, item.Peers);
    }

    [Fact]
    public async Task A_swarm_with_only_leechers_and_no_seeds_is_not_reported_as_zero_peers()
    {
        var item = await Snapshot("downloading", 0.4, numSeeds: 0, numLeechs: 3);

        Assert.Equal(3, item.Peers);
    }

    /// <summary>
    /// #815: qBittorrent's own "private" field on torrents/info round-trips
    /// into IsPrivate exactly, and is null (unknown, not false) when an older
    /// qBittorrent does not send it at all.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, null)]
    public async Task Privacy_round_trips_from_qbittorrent(bool? clientReports, bool? expected)
    {
        var item = await Snapshot("downloading", 0.4, isPrivate: clientReports);

        Assert.Equal(expected, item.IsPrivate);
    }

    private static async Task<DownloadQueueItem> Snapshot(string state, double progress, int numSeeds = 1, int numLeechs = 0, bool? isPrivate = null)
    {
        // qBittorrent 5.2.1's recorded list of a download in progress, in the state, progress, swarm and privacy under test.
        // A privacy the client does not send is taken out of the reply.
        var torrentsInfo = RecordedQbittorrentRig.Info(
            RecordedQbittorrentRig.Downloading,
            ("category", "movies"), ("state", state), ("progress", progress), ("num_seeds", numSeeds), ("num_leechs", numLeechs), ("private", isPrivate));
        var handler = RecordedQbittorrentRig.Handler(torrentsInfo);
        var client = new QbittorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
