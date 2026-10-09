using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1193: the strike rules apply at any percent below complete. A client's own "stalled" flag is never read as
/// Stalled by itself (a download may only be closing its last blocks); Deluno's own no-progress window decides.
/// A torrent stuck at 99.95% for want of one piece is a real stall and is struck like one at 50%.
/// </summary>
public sealed class NearlyFinishedDownloadsFollowTheStrikeRulesTests
{
    private static readonly DateTimeOffset Added = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(99.95, 45, DownloadQueueStatuses.Stalled)]
    [InlineData(99.99, 45, DownloadQueueStatuses.Stalled)]
    [InlineData(50, 45, DownloadQueueStatuses.Stalled)]
    [InlineData(99.95, 10, DownloadQueueStatuses.Downloading)]
    [InlineData(99.99, 10, DownloadQueueStatuses.Downloading)]
    [InlineData(100, 45, DownloadQueueStatuses.Downloading)]
    public void The_no_progress_rule_stalls_a_download_at_any_percent_only_after_its_window(double progress, int minutesWithoutProgress, string expected)
    {
        var captured = Added.AddMinutes(minutesWithoutProgress);
        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(Item(progress), captured, Added);

        Assert.Equal(expected, reconciled.Status);
    }

    [Fact]
    public void A_download_stuck_at_99_95_gets_a_stalled_finding_after_the_window_and_none_before()
    {
        var evaluator = new DownloadHealthEvaluator();
        var stalled = DownloadClientHelpers.ReconcileTorrentStatus(Item(99.95), Added.AddMinutes(45), Added);
        var finding = Assert.Single(evaluator.Evaluate(Snapshot(stalled), Added.AddMinutes(45)), f => f.Kind == DownloadHealthStrikes.ClientStalled);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
        Assert.False(DownloadHealthStrikes.IsFinishedOrHandedOff(stalled.Status, stalled.Progress));

        var finishing = DownloadClientHelpers.ReconcileTorrentStatus(Item(99.99), Added.AddMinutes(5), Added);
        Assert.Equal(DownloadQueueStatuses.Downloading, finishing.Status);
        Assert.DoesNotContain(evaluator.Evaluate(Snapshot(finishing), Added.AddMinutes(5)), f => f.Kind == DownloadHealthStrikes.ClientStalled);
    }

    [Theory]
    [InlineData("stalledDL", 0.99995, DownloadQueueStatuses.Downloading)]
    [InlineData("stalledDL", 0.4, DownloadQueueStatuses.Downloading)]
    [InlineData("error", 0.99995, DownloadQueueStatuses.Stalled)]
    [InlineData("missingFiles", 0.99995, DownloadQueueStatuses.Stalled)]
    [InlineData("checkingDL", 0.4, DownloadQueueStatuses.NotTransferring)]
    [InlineData("moving", 1.0, DownloadQueueStatuses.NotTransferring)]
    public void Qbittorrent_does_not_read_its_own_stalled_flag_as_stalled(string state, double progress, string expected)
        => Assert.Equal(expected, new QbittorrentDownloadClient().NormalizeStatus(state, progress));

    [Theory]
    [InlineData("Stalled", 99.995, DownloadQueueStatuses.Downloading)]
    [InlineData("Stalled", 30, DownloadQueueStatuses.Downloading)]
    [InlineData("Stalled", 100, DownloadQueueStatuses.ImportReady)]
    [InlineData("Error", 99.995, DownloadQueueStatuses.Stalled)]
    [InlineData("Checking", 50, DownloadQueueStatuses.NotTransferring)]
    public void The_shared_text_status_does_not_read_a_stalled_flag_as_stalled(string state, double progress, string expected)
        => Assert.Equal(expected, DownloadClientHelpers.NormalizeTextStatus(state, progress));

    [Fact]
    public void Only_a_complete_download_counts_as_finished_by_percent()
    {
        Assert.False(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Downloading, 99.999));
        Assert.True(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Downloading, 100));
    }

    private static RecoveryQueueSnapshot Snapshot(DownloadQueueItem item)
        => new(item.Status, item.ErrorMessage, item.SourcePath, item.SpeedMbps, item.AddedUtc, item.EtaSeconds, item.ReleaseName, item.Peers,
            ProgressPercent: item.Progress, SizeBytes: item.SizeBytes);

    private static DownloadQueueItem Item(double progress)
        => new(
            Id: "abc", ClientId: "transmission-1", ClientName: "Transmission", Protocol: "transmission", MediaType: "movies",
            Title: "Nosferatu", ReleaseName: "Nosferatu.1922.1080p", Category: "movies", Status: DownloadQueueStatuses.Downloading,
            Progress: progress, SpeedMbps: 0, EtaSeconds: 0, SizeBytes: 1_580_000_000, DownloadedBytes: (long)(1_580_000_000 * progress / 100),
            Peers: 7, IndexerName: "Indexer", ErrorMessage: null, AddedUtc: Added);
}
