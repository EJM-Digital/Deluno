using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #659: found live on the rig proving #639. Transmission grabbed an episode's
/// upgrade, connected two peers, and fourteen minutes later had gained nothing
/// - <c>rateDownload 0</c>, <c>percentDone</c> barely moving. The shared rule
/// from #650, <see cref="DownloadClientHelpers.ReconcileTorrentStatus"/>, only
/// stalled a torrent with unknown size and no peers, so this one sat
/// "downloading" forever: never struck, never blocklisted, no replacement ever
/// tried.
///
/// <para>These exercise the rule directly with a persisted
/// <c>lastProgressUtc</c> - when the item's downloaded bytes were last seen to
/// increase - the same value <see cref="Deluno.Platform.Data.IDownloadHealthRepository.RecordDownloadProgressAsync"/>
/// hands back on every poll. One rule, so Transmission, qBittorrent and Deluge
/// all read the same answer once their snapshot passes through it.</para>
/// </summary>
public sealed class ANoProgressTorrentIsStalledWhateverItsPeersTests
{
    private static readonly DateTimeOffset AddedUtc = DateTimeOffset.Parse("2026-09-25T07:05:00Z");

    [Fact]
    public void No_bytes_gained_for_the_stall_window_is_stalled_even_with_connected_peers()
    {
        var lastProgressUtc = AddedUtc; // never increased since the grab
        var capturedUtc = AddedUtc.Add(Deluno.Platform.Contracts.DownloadHealthStrikes.DefaultWindow);
        var item = Downloading(sizeBytes: 3_580_727_698, downloadedBytes: 716_145, peers: 2, capturedUtc: capturedUtc);

        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc, lastProgressUtc);

        Assert.Equal(DownloadQueueStatuses.Stalled, reconciled.Status);
    }

    [Fact]
    public void Still_within_the_stall_window_since_the_last_gain_is_not_stalled()
    {
        var lastProgressUtc = AddedUtc;
        var capturedUtc = AddedUtc.AddMinutes(14); // the rig's own timing: 14 minutes, no gain, peers connected
        var item = Downloading(sizeBytes: 3_580_727_698, downloadedBytes: 716_145, peers: 2, capturedUtc: capturedUtc);

        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc, lastProgressUtc);

        Assert.Equal(DownloadQueueStatuses.Downloading, reconciled.Status);
    }

    [Fact]
    public void A_slow_but_moving_torrent_is_never_stalled_because_every_gain_moves_the_clock()
    {
        // Every observation gained a few bytes, so lastProgressUtc tracks
        // "now" at each poll and never falls behind by a full window, however
        // long the download has been running in total.
        var capturedUtc = AddedUtc.AddHours(6);
        var lastProgressUtc = capturedUtc.AddMinutes(-5); // its most recent gain, five minutes ago
        var item = Downloading(sizeBytes: 3_580_727_698, downloadedBytes: 500_000_000, peers: 2, capturedUtc: capturedUtc);

        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc, lastProgressUtc);

        Assert.Equal(DownloadQueueStatuses.Downloading, reconciled.Status);
    }

    [Fact]
    public void No_persisted_progress_yet_is_not_treated_as_stalled()
    {
        // A caller with nothing recorded yet (this item's first sighting)
        // passes null rather than guessing; that must never read as "stalled
        // since the beginning of time".
        var capturedUtc = AddedUtc.AddHours(2);
        var item = Downloading(sizeBytes: 3_580_727_698, downloadedBytes: 0, peers: 2, capturedUtc: capturedUtc);

        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc, lastProgressUtc: null);

        Assert.Equal(DownloadQueueStatuses.Downloading, reconciled.Status);
    }

    private static DownloadQueueItem Downloading(long sizeBytes, long downloadedBytes, int peers, DateTimeOffset capturedUtc)
        => new(
            Id: "abc123",
            ClientId: "transmission-1",
            ClientName: "Transmission",
            Protocol: "transmission",
            MediaType: "tv",
            Title: "The Last of Us",
            ReleaseName: "The.Last.of.Us.S01E07.Left.Behind.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb",
            Category: "tv",
            Status: DownloadQueueStatuses.Downloading,
            Progress: sizeBytes > 0 ? Math.Round(downloadedBytes * 100d / sizeBytes, 1) : 0,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: sizeBytes,
            DownloadedBytes: downloadedBytes,
            Peers: peers,
            IndexerName: "Indexer",
            ErrorMessage: null,
            AddedUtc: AddedUtc);
}
