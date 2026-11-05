using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>Activity says what an outside program's removal actually did, part by part, for each flag combination.</summary>
public sealed class OutsideProgramRemovalActivityTests
{
    private static string Say(bool removed, bool blocklisted, bool searched, DownloadClientTelemetryService.RemovalOutcome outcome)
        => DownloadClientTelemetryService.BuildQueueRemovalMessage("Film", "qBittorrent", removed, blocklisted, searched, outcome);

    [Fact]
    public void Remove_with_files_blocklist_and_search_names_all_three()
        => Assert.Equal(
            "An outside program removed \"Film\" from the Queue: removed from qBittorrent and deleted its files, blocklisted the release, started a new search.",
            Say(true, true, true, DownloadClientTelemetryService.RemovalOutcome.Deleted));

    [Fact]
    public void Remove_alone_says_only_the_removal()
        => Assert.Equal(
            "An outside program removed \"Film\" from the Queue: removed from qBittorrent and deleted its files.",
            Say(true, false, false, DownloadClientTelemetryService.RemovalOutcome.Deleted));

    [Fact]
    public void Removing_but_keeping_the_files_says_the_files_stayed_on_disk()
        => Assert.Equal(
            "An outside program removed \"Film\" from the Queue: removed from qBittorrent and left its files on disk, blocklisted the release.",
            Say(true, true, false, DownloadClientTelemetryService.RemovalOutcome.FilesKept));

    [Fact]
    public void A_download_the_client_no_longer_had_says_nothing_was_deleted()
        => Assert.Equal(
            "An outside program removed \"Film\" from the Queue: already gone from qBittorrent, so nothing was deleted, started a new search.",
            Say(true, false, true, DownloadClientTelemetryService.RemovalOutcome.AlreadyGone));

    [Fact]
    public void Stopping_tracking_only_says_the_client_still_has_it()
        => Assert.Equal(
            "An outside program removed \"Film\" from the Queue: stopped tracking it, and qBittorrent still has it, untouched, blocklisted the release.",
            Say(false, true, false, DownloadClientTelemetryService.RemovalOutcome.NotTouched));
}
