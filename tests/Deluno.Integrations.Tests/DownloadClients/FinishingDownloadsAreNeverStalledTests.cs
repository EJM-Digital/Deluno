using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1193: Nosferatu briefly showed "Stalled - 100%" at 99.99% while its client said it was downloading. A download
/// that is finishing, with nothing left to fetch or only moving or checking, is never Stalled, whatever the client.
/// </summary>
public sealed class FinishingDownloadsAreNeverStalledTests
{
    private static readonly DateTimeOffset Added = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(99.99, DownloadQueueStatuses.Downloading)]
    [InlineData(99.995, DownloadQueueStatuses.Downloading)]
    [InlineData(100, DownloadQueueStatuses.Downloading)]
    [InlineData(60, DownloadQueueStatuses.Stalled)]
    [InlineData(99.5, DownloadQueueStatuses.Stalled)]
    public void The_no_progress_rule_only_stalls_a_download_with_real_data_left(double progress, string expected)
    {
        var captured = Added.AddMinutes(45);
        var reconciled = DownloadClientHelpers.ReconcileTorrentStatus(Item(progress), captured, Added);

        Assert.Equal(expected, reconciled.Status);
    }

    [Theory]
    [InlineData("stalledDL", 0.99995, DownloadQueueStatuses.Downloading)]
    [InlineData("stalledDL", 0.4, DownloadQueueStatuses.Stalled)]
    [InlineData("error", 0.99995, DownloadQueueStatuses.Stalled)]
    [InlineData("checkingDL", 0.4, DownloadQueueStatuses.NotTransferring)]
    [InlineData("moving", 1.0, DownloadQueueStatuses.NotTransferring)]
    public void Qbittorrent_reads_a_finishing_stalled_state_as_downloading(string state, double progress, string expected)
        => Assert.Equal(expected, new QbittorrentDownloadClient().NormalizeStatus(state, progress));

    [Theory]
    [InlineData("Stalled", 99.995, DownloadQueueStatuses.ImportReady)]
    [InlineData("Stalled", 30, DownloadQueueStatuses.Stalled)]
    [InlineData("Error", 99.995, DownloadQueueStatuses.Stalled)]
    [InlineData("Checking", 50, DownloadQueueStatuses.NotTransferring)]
    public void The_shared_text_status_does_not_call_a_finishing_download_stalled(string state, double progress, string expected)
        => Assert.Equal(expected, DownloadClientHelpers.NormalizeTextStatus(state, progress));

    [Fact]
    public void The_health_rules_treat_a_finishing_download_as_finished()
        => Assert.True(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Stalled, 99.995));

    private static DownloadQueueItem Item(double progress)
        => new(
            Id: "abc", ClientId: "transmission-1", ClientName: "Transmission", Protocol: "transmission", MediaType: "movies",
            Title: "Nosferatu", ReleaseName: "Nosferatu.1922.1080p", Category: "movies", Status: DownloadQueueStatuses.Downloading,
            Progress: progress, SpeedMbps: 0, EtaSeconds: 0, SizeBytes: 1_580_000_000, DownloadedBytes: (long)(1_580_000_000 * progress / 100),
            Peers: 7, IndexerName: "Indexer", ErrorMessage: null, AddedUtc: Added);
}
