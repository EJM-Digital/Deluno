using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #280 — the Processing stage could show a count it could never clear. The
/// import job is keyed to the refined output path and the queue item to the
/// original download path, so nothing matched them up. These cover the rule
/// that reads the hand-off instead.
/// </summary>
public sealed class ProcessorHandoffQueueStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    private static ProcessorHandoffItem Handoff(string status, string? importJobId = null) =>
        new(
            Id: "handoff-1",
            LibraryId: "movies-main",
            MediaType: "movies",
            ClientId: "qb-1",
            QueueItemId: "torrent-hash-1",
            ReleaseName: "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-DELUNO",
            SourcePath: @"C:\Deluno\Downloads-Complete\Movies\Big.Buck.Bunny.2008.2160p.WEB-DL.x265-DELUNO",
            ProcessorName: "Weir",
            Status: status,
            OutputPath: status == "completed" ? @"C:\Deluno\Refined\Movies\Big Buck Bunny (2008).mkv" : null,
            ImportJobId: importJobId,
            FailureMessage: null,
            CreatedUtc: Now,
            UpdatedUtc: Now);

    private static IReadOnlyDictionary<string, JobQueueItem> Jobs(params (string Id, string Status)[] jobs) =>
        jobs.ToDictionary(
            job => job.Id,
            job => new JobQueueItem(job.Id, "filesystem.import.execute", "processor-output-watch", job.Status, null, 0,
                Now, Now, null, null, null, null, null, null, null, null, null, 3, null, null),
            StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("waiting")]
    [InlineData("submitted")]
    [InlineData("accepted")]
    [InlineData("started")]
    public void A_processor_still_holding_the_item_keeps_it_waiting(string status)
    {
        Assert.Equal(
            DownloadQueueStatuses.WaitingForProcessor,
            ProcessorHandoffQueueStatus.Resolve(Handoff(status), Jobs(), DownloadQueueStatuses.ImportReady));
    }

    [Fact]
    public void A_failed_hand_off_reads_as_a_processing_failure()
    {
        Assert.Equal(
            DownloadQueueStatuses.ProcessingFailed,
            ProcessorHandoffQueueStatus.Resolve(Handoff("failed"), Jobs(), DownloadQueueStatuses.ImportReady));
    }

    [Fact]
    public void A_finished_processor_with_no_import_job_yet_reads_as_processed()
    {
        Assert.Equal(
            DownloadQueueStatuses.Processed,
            ProcessorHandoffQueueStatus.Resolve(Handoff("completed"), Jobs(), DownloadQueueStatuses.ImportReady));
    }

    [Theory]
    [InlineData("queued", DownloadQueueStatuses.ImportQueued)]
    [InlineData("running", DownloadQueueStatuses.ImportQueued)]
    [InlineData("completed", DownloadQueueStatuses.Imported)]
    [InlineData("failed", DownloadQueueStatuses.ImportFailed)]
    public void The_import_job_decides_once_the_processor_is_done(string jobStatus, string expected)
    {
        Assert.Equal(
            expected,
            ProcessorHandoffQueueStatus.Resolve(Handoff("completed", "job-1"), Jobs(("job-1", jobStatus)), DownloadQueueStatuses.ImportReady));
    }

    /// <summary>
    /// The headline case. The item is still seeding, so it stays in the client
    /// queue reporting importReady; the import that finished it happened under a
    /// path the queue has never heard of.
    /// </summary>
    [Fact]
    public void A_seeding_torrent_whose_refined_import_completed_leaves_the_processing_stage()
    {
        Assert.Equal(
            DownloadQueueStatuses.Imported,
            ProcessorHandoffQueueStatus.Resolve(Handoff("completed", "job-1"), Jobs(("job-1", "completed")), DownloadQueueStatuses.ImportReady));
    }

    /// <summary>
    /// Jobs are read as a recent window. An import that has aged out of it is
    /// finished work, and must not fall back to a Processing status and pin the
    /// stage open again.
    /// </summary>
    [Fact]
    public void An_import_job_that_has_aged_out_of_the_window_is_still_finished()
    {
        Assert.Equal(
            DownloadQueueStatuses.Imported,
            ProcessorHandoffQueueStatus.Resolve(Handoff("completed", "job-long-gone"), Jobs(("job-1", "completed")), DownloadQueueStatuses.ImportReady));
    }
}
