using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Rig, 9 Oct (#1182): a wrong-episode repair moved an episode's file to the recycle bin, the episode was Missing, and
/// search sent the very release again. The torrent was still finished in the client, so the old completed hand-off
/// stood in for it and nothing was ever imported: Transfers read "Ready to import" for a day. A release sent again
/// since its hand-off ended is a new acquisition and is handed over again.
/// </summary>
public sealed class AFinishedHandOffDoesNotHideAReleaseSentAgainTests
{
    private static readonly DateTimeOffset HandoffEnded = DateTimeOffset.Parse("2026-10-08T06:58:17Z");
    private static readonly DateTimeOffset SentAgain = DateTimeOffset.Parse("2026-10-08T08:19:17Z");
    private const string Release = "The.Simpsons.S38E02.Diary.of.a.Chimpy.Kid.1080p.DSNP.WEB-DL.DDP5.1.H.264-NTb";

    [Fact]
    public async Task A_release_sent_again_since_its_hand_off_ended_is_handed_over_again()
    {
        Assert.False(await Dealt(withNewerSend: true));
    }

    [Fact]
    public async Task Without_a_newer_send_a_finished_hand_off_still_means_it_was_dealt_with()
    {
        Assert.True(await Dealt(withNewerSend: false));
    }

    // #1182: a hand-off settled Dismissed a second before its import job completed is a finished import all the same.
    [Fact]
    public async Task A_dismissed_hand_off_whose_import_completed_does_not_hide_a_release_sent_again()
    {
        Assert.False(await Dealt(withNewerSend: true, status: "dismissed", jobStatus: "completed"));
    }

    [Fact]
    public async Task A_dismissed_hand_off_whose_import_did_not_complete_still_means_it_was_dealt_with()
    {
        Assert.True(await Dealt(withNewerSend: true, status: "dismissed", jobStatus: "failed"));
    }

    // Rig, 17 Oct (resend.single-file.takeover): the download was removed and added again in the client. Removing it closed
    // the hand-off (dismissed) a moment before the client took the same torrent again, and the client stamps an add time in
    // whole seconds, so the dismissal's time was later than the new add time and the new download read as the old one: no
    // second hand-off was ever sent. The hand-off was made after the first add; an add after the hand-off was made is new.
    [Fact]
    public async Task A_download_added_again_is_new_though_the_old_hand_off_was_closed_after_it_was_added()
    {
        var made = DateTimeOffset.Parse("2026-10-17T04:34:18.826Z");
        var closedByTheRemoval = DateTimeOffset.Parse("2026-10-17T04:34:26.500Z");
        var addedAgain = DateTimeOffset.Parse("2026-10-17T04:34:26Z");
        Assert.False(await Dealt(withNewerSend: false, status: "dismissed", jobStatus: "failed",
            handoffMade: made, handoffUpdated: closedByTheRemoval, downloadAdded: addedAgain));
    }

    [Fact]
    public async Task The_download_a_dismissed_hand_off_was_made_for_is_still_dealt_with()
    {
        var made = DateTimeOffset.Parse("2026-10-17T04:34:18.826Z");
        var closed = DateTimeOffset.Parse("2026-10-17T04:34:26.500Z");
        Assert.True(await Dealt(withNewerSend: false, status: "dismissed", jobStatus: "failed",
            handoffMade: made, handoffUpdated: closed, downloadAdded: made.AddSeconds(-20)));
    }

    private static async Task<bool> Dealt(
        bool withNewerSend, string status = "completed", string jobStatus = "completed",
        DateTimeOffset? handoffMade = null, DateTimeOffset? handoffUpdated = null, DateTimeOffset? downloadAdded = null)
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.FindProcessorHandoffForDownloadAsync("client-1", "hash-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffItem(
                "handoff-1", "library-1", "tv", "client-1", "hash-1", Release, "/downloads/" + Release, "Weir",
                status, "/ready/" + Release, "job-1", null, handoffMade ?? HandoffEnded.AddMinutes(-1), handoffUpdated ?? HandoffEnded));
        processors.Setup(repo => repo.ListProcessorHandoffsForReleaseAsync(Release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());

        var sends = new List<DownloadDispatchItem> { Dispatch("imported", HandoffEnded.AddMinutes(-3)) };
        if (withNewerSend)
        {
            sends.Add(Dispatch(null, SentAgain));
        }

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobQueueItem("job-1", "filesystem.import.execute", "processor", jobStatus, null, 1, HandoffEnded, HandoffEnded, HandoffEnded, HandoffEnded, null, null, null, null, null, null, null, 3, HandoffEnded, null));
        jobs.Setup(repo => repo.ListDownloadDispatchesAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>(), true, "client-1"))
            .ReturnsAsync(sends);

        var item = new DownloadQueueItem(
            "hash-1", "client-1", "qBittorrent", "qbittorrent", "tv", "The Simpsons", Release, "tv",
            DownloadQueueStatuses.ImportReady, 1.0, 0, 0, 100, 100, 0, "Lab", null, downloadAdded ?? HandoffEnded.AddMinutes(-10),
            SourcePath: "/downloads/" + Release);

        return await WorkPlanner.HasBeenDealtWithAsync(item, processors.Object, null, CancellationToken.None, jobs.Object);
    }

    private static DownloadDispatchItem Dispatch(string? importStatus, DateTimeOffset created)
        => new(
            "dispatch-" + created.ToUnixTimeSeconds(), "library-1", "tv", "episode", "episode-2", Release, "Lab", "client-1", "qBittorrent",
            "sent", null, created, "succeeded", created, 200, null, null, null, created, "hash-1", 0,
            importStatus, null, null, null, null, null, null);
}
