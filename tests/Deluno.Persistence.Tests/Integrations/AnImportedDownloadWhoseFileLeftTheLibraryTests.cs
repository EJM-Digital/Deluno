using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Rig, 9 Oct (#1182): two Simpsons S38E02 downloads read "Ready to import" for a day, and the Dashboard counted them as
/// importing over one card. Both had a completed hand-off, a completed import job and a delivered report. The episode's
/// file had gone to the recycle bin afterwards (a wrong-episode repair), so the title had no file, and the planner never
/// imports a download it already imported. "Ready to import" promised an import nobody would make.
/// </summary>
public sealed class AnImportedDownloadWhoseFileLeftTheLibraryTests
{
    private static readonly DateTimeOffset Imported = DateTimeOffset.Parse("2026-10-08T06:58:22Z");
    private static readonly DateTimeOffset SentAgain = DateTimeOffset.Parse("2026-10-08T08:19:17Z");
    private const string Release = "The.Simpsons.S38E02.Diary.of.a.Chimpy.Kid.1080p.DSNP.WEB-DL.DDP5.1.H.264-NTb";

    [Fact]
    public async Task A_finished_hand_off_and_import_with_the_file_gone_reads_as_history_not_Imported_or_Ready_to_import()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3))],
            new Files(false),
            CancellationToken.None,
            [Handoff("completed", "job-1")]);

        Assert.Equal(DownloadQueueStatuses.ImportedFileLeft, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public async Task The_same_release_sent_again_since_is_a_new_attempt_and_reads_Ready_to_import()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3)), Dispatch(importStatus: null, created: SentAgain)],
            new Files(false),
            CancellationToken.None,
            [Handoff("completed", "job-1")]);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public async Task A_download_with_no_finished_hand_off_still_reads_Ready_to_import_when_its_file_is_gone()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3))],
            new Files(false),
            CancellationToken.None,
            []);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(result.Queue).Status);
    }

    // Rig, 9 Oct: the settler dismissed the hand-off one second before its import job completed.
    [Fact]
    public async Task A_dismissed_hand_off_whose_import_job_completed_reads_exactly_like_a_completed_one()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3))],
            new Files(false),
            CancellationToken.None,
            [Handoff("dismissed", "job-1")],
            [Job("job-1", "completed")]);

        Assert.Equal(DownloadQueueStatuses.ImportedFileLeft, Assert.Single(result.Queue).Status);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("queued")]
    public async Task A_dismissed_hand_off_whose_import_job_did_not_complete_is_not_an_import_that_happened(string jobStatus)
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3))],
            new Files(false),
            CancellationToken.None,
            [Handoff("dismissed", "job-1")],
            [Job("job-1", jobStatus)]);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public async Task A_dismissed_hand_off_with_a_completed_import_and_a_newer_send_is_a_new_attempt()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3)), Dispatch(importStatus: null, created: SentAgain)],
            new Files(false),
            CancellationToken.None,
            [Handoff("dismissed", "job-1")],
            [Job("job-1", "completed")]);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(result.Queue).Status);
    }

    // Rig, 9 Oct (#1200): Dismiss on a "download was gone" row made the download read "Ready to import" in Transfers and the Dashboard.
    [Fact]
    public async Task A_dismissed_source_gone_hand_off_reads_as_closed_history_never_Ready_to_import()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3))],
            new Files(false),
            CancellationToken.None,
            [GoneHandoff("dismissed")]);

        Assert.Equal(DownloadQueueStatuses.FilesGone, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public async Task A_dismissed_source_gone_hand_off_followed_by_a_new_send_is_a_new_attempt()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch(importStatus: "imported", created: Imported.AddMinutes(-3)), Dispatch(importStatus: null, created: SentAgain)],
            new Files(false),
            CancellationToken.None,
            [GoneHandoff("dismissed")]);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public void Only_a_dismissed_hand_off_with_the_source_gone_sentence_is_closed_as_gone()
    {
        Assert.True(DownloadAttempts.SourceGoneClosed(GoneHandoff("dismissed")));
        Assert.False(DownloadAttempts.SourceGoneClosed(GoneHandoff("source-gone")));
        Assert.False(DownloadAttempts.SourceGoneClosed(Handoff("dismissed", null)));
    }

    // #1200: the client still lists the download as done, but its files are not on disk.
    [Fact]
    public void A_download_listed_as_done_whose_files_are_not_on_disk_says_its_files_are_gone()
    {
        var result = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(
            Snapshot(DownloadQueueStatuses.ImportReady), path => path.Replace((char)92, (char)47) == "/downloads");

        var item = Assert.Single(result.Queue);
        Assert.Equal(DownloadQueueStatuses.FilesGone, item.Status);
        Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));
    }

    [Fact]
    public void A_download_whose_files_are_there_or_whose_folder_cannot_be_seen_is_left_alone()
    {
        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(
            Snapshot(DownloadQueueStatuses.ImportReady), _ => true).Queue).Status);
        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(
            Snapshot(DownloadQueueStatuses.ImportReady), _ => false).Queue).Status);
        Assert.Equal(DownloadQueueStatuses.Downloading, Assert.Single(DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(
            Snapshot(DownloadQueueStatuses.Downloading), path => path.Replace((char)92, (char)47) == "/downloads").Queue).Status);
    }

    private static ProcessorHandoffItem GoneHandoff(string status)
        => Handoff(status, null) with { FailureMessage = ProcessorFailureClasses.SourceGoneMessage("Weir") };

    private static JobQueueItem Job(string id, string status)
        => new(id, "filesystem.import.execute", "processor", status, null, 1, Imported, Imported, Imported, Imported, null, null, null, null, null, null, null, 3, Imported, null);

    [Theory]
    [InlineData("succeeded", null, true)]
    [InlineData("succeeded", "", true)]
    [InlineData("succeeded", "imported", false)]
    [InlineData("succeeded", "failed", false)]
    [InlineData("failed", null, false)]
    public void Only_a_taken_and_not_yet_imported_send_after_the_hand_off_ended_counts(string grab, string? import, bool expected)
    {
        var sends = new[] { Dispatch(import, SentAgain, grab) };

        Assert.Equal(expected, DownloadAttempts.SentAgainSince(sends, "client-1", Release, Imported));
    }

    [Fact]
    public void A_send_made_before_the_hand_off_ended_or_of_another_release_does_not_count()
    {
        Assert.False(DownloadAttempts.SentAgainSince([Dispatch(null, Imported.AddMinutes(-3))], "client-1", Release, Imported));
        Assert.False(DownloadAttempts.SentAgainSince([Dispatch(null, SentAgain)], "client-1", "Another.Release", Imported));
        Assert.False(DownloadAttempts.SentAgainSince([Dispatch(null, SentAgain)], "client-2", Release, Imported));
    }

    private sealed class Files(bool? hasFile) : IDispatchTitleUnmonitorService
    {
        public bool CanHandle(string entityType) => true;
        public Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool?> HasFileAsync(string entityType, string entityId, CancellationToken cancellationToken) => Task.FromResult(hasFile);
    }

    private static ProcessorHandoffItem Handoff(string status, string? importJobId)
        => new(
            "handoff-1", "library-1", "tv", "client-1", "hash-1", Release, "/downloads/" + Release, "Weir",
            status, "/ready/" + Release, importJobId, null, Imported.AddMinutes(-3), Imported);

    private static DownloadClientTelemetrySnapshot Snapshot(string status)
        => new(
            "client-1", "qBittorrent", "qbittorrent", "http://localhost:8080/", "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            [new DownloadQueueItem(
                "hash-1", "client-1", "qBittorrent", "qbittorrent", "tv", "The Simpsons", Release, "tv",
                status, 1.0, 0, 0, 100, 100, 0, "Lab", null, Imported.AddMinutes(-10),
                SourcePath: "/downloads/" + Release)],
            [],
            Imported);

    private static DownloadDispatchItem Dispatch(string? importStatus, DateTimeOffset created, string grab = "succeeded")
        => new(
            "dispatch-" + created.ToUnixTimeSeconds(), "library-1", "tv", "episode", "episode-2", Release, "Lab", "client-1", "qBittorrent",
            importStatus == "imported" ? "archived" : "sent", null, created, grab, created, 200, null, null, null, created, "hash-1", 0,
            importStatus, null, null, null, null, null, null);
}
