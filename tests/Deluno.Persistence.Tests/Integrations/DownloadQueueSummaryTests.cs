using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #280 — the dashboard subtracts the processor share from the processing
/// bucket to get the import share. That only reads correctly if the first is a
/// subset of the second, so the rule is pinned here rather than clamped in the
/// view.
/// </summary>
public sealed class DownloadQueueSummaryTests
{
    private static readonly string[] EveryStatus =
    [
        DownloadQueueStatuses.Downloading,
        DownloadQueueStatuses.Queued,
        DownloadQueueStatuses.ImportReady,
        DownloadQueueStatuses.Stalled,
        DownloadQueueStatuses.Processing,
        DownloadQueueStatuses.Processed,
        DownloadQueueStatuses.ProcessingFailed,
        DownloadQueueStatuses.WaitingForProcessor,
        DownloadQueueStatuses.ImportQueued,
        DownloadQueueStatuses.Imported,
        DownloadQueueStatuses.ImportFailed,
        DownloadQueueStatuses.Completed
    ];

    private static DownloadQueueItem Item(string status) =>
        new($"item-{status}", "qb-1", "qBittorrent", "qbittorrent", "movies", "Title", $"Release.{status}",
            "deluno-movies", status, 100, 0, 0, 1024, 0, 0, "Fixture", null, DateTimeOffset.Parse("2026-08-26T00:00:00Z"));

    private static DownloadQueueItem Torrent(string protocol, string status, double progress) =>
        new($"item-{protocol}-{status}-{progress}", "c1", "Client", protocol, "movies", "Title", $"Release.{status}",
            "deluno-movies", status, progress, 0, 0, 1024, 0, 0, "Fixture", null, DateTimeOffset.Parse("2026-08-26T00:00:00Z"));

    /// <summary>#1098: the Live screen says how many torrents each client is seeding.</summary>
    [Fact]
    public void Seeding_counts_finished_torrents_still_being_shared_and_nothing_else()
    {
        var summary = DownloadQueueSummary.Of(
        [
            Torrent("qbittorrent", DownloadQueueStatuses.Imported, 100),
            Torrent("deluge", DownloadQueueStatuses.Completed, 100),
            Torrent("transmission", DownloadQueueStatuses.ImportFailed, 100),
            // Still downloading, paused, or dead: not seeding.
            Torrent("qbittorrent", DownloadQueueStatuses.Downloading, 54),
            Torrent("qbittorrent", DownloadQueueStatuses.Paused, 100),
            Torrent("qbittorrent", DownloadQueueStatuses.DeadRelease, 100),
            // Usenet has no sharing phase.
            Torrent("sabnzbd", DownloadQueueStatuses.Imported, 100),
            Torrent("nzbget", DownloadQueueStatuses.Completed, 100)
        ]);

        Assert.Equal(3, summary.SeedingCount);
    }

    [Fact]
    public void A_client_with_an_empty_queue_is_seeding_nothing()
        => Assert.Equal(0, DownloadQueueSummary.Of([]).SeedingCount);

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Waiting_for_a_processor_is_always_also_processing(string status)
    {
        if (DownloadQueueSummary.IsWaitingForProcessor(status))
        {
            Assert.True(DownloadQueueSummary.IsProcessing(status));
        }
    }

    [Fact]
    public void The_import_share_is_never_negative_for_any_mix_of_statuses()
    {
        var summary = DownloadQueueSummary.Of(EveryStatus.Select(Item));

        Assert.True(summary.WaitingForProcessorCount <= summary.ProcessingCount);
        Assert.Equal(1, summary.WaitingForProcessorCount);
        Assert.Equal(5, summary.ProcessingCount);
    }

    [Fact]
    public void A_queue_of_nothing_but_processor_waits_leaves_no_import_share()
    {
        var summary = DownloadQueueSummary.Of(Enumerable.Range(0, 3).Select(_ => Item(DownloadQueueStatuses.WaitingForProcessor)));

        Assert.Equal(3, summary.ProcessingCount);
        Assert.Equal(3, summary.WaitingForProcessorCount);
        Assert.Equal(0, summary.ProcessingCount - summary.WaitingForProcessorCount);
    }

    public static TheoryData<string> AllStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var status in EveryStatus)
        {
            data.Add(status);
        }
        return data;
    }
}
