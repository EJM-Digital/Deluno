using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1155, #1182: a download whose latest fetch was imported is never handed to a processor again, a release sent again on
/// purpose is a new acquisition, and a hand-off counts as finished only when its import ran to its end.
/// </summary>
public sealed class ImportedDownloadsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    private static DownloadDispatchItem Dispatch(string clientId, string release, DateTimeOffset created, string? importStatus) => new(
        "d-" + created.Ticks, "lib", "movies", "movie", "m1", release, "Indexer", clientId, "Client", "completed", null, created,
        null, null, null, null, null, null, null, null, null, importStatus, null, null, null, null, null, null);

    private static ProcessorHandoffItem Handoff(string status, string? importJobId) => new(
        "h1", "lib", "movie", "client-1", "q1", "Heat.1995", "/downloads/heat", "Weir", status, null, importJobId, null, Now, Now);

    [Fact]
    public void The_latest_fetch_decides_and_an_older_import_does_not_count_when_it_was_fetched_again()
    {
        var dispatches = new[]
        {
            Dispatch("client-1", "Heat.1995.1080p", Now.AddDays(-2), "imported"),
            Dispatch("client-1", "Heat.1995.1080p", Now.AddDays(-1), null)
        };

        Assert.False(ImportedDownloads.WasImported(dispatches, "client-1", "Heat.1995.1080p"));
    }

    [Fact]
    public void A_release_whose_latest_fetch_was_imported_is_imported_whatever_the_case_of_the_words()
    {
        var dispatches = new[]
        {
            Dispatch("client-1", "Heat.1995.1080p", Now.AddDays(-2), null),
            Dispatch("CLIENT-1", "heat.1995.1080p.mkv", Now.AddDays(-1), "IMPORTED")
        };

        Assert.True(ImportedDownloads.WasImported(dispatches, "client-1", "Heat.1995.1080p"));
    }

    [Fact]
    public void A_fetch_by_another_client_or_of_another_release_says_nothing()
    {
        var dispatches = new[]
        {
            Dispatch("client-2", "Heat.1995.1080p", Now, "imported"),
            Dispatch("client-1", "Casino.1995.1080p", Now, "imported")
        };

        Assert.False(ImportedDownloads.WasImported(dispatches, "client-1", "Heat.1995.1080p"));
        Assert.False(ImportedDownloads.WasImported([], "client-1", "Heat.1995.1080p"));
    }

    [Fact]
    public async Task Asking_the_queue_looks_at_the_clients_five_hundred_latest_including_archived_ones()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(j => j.ListDownloadDispatchesAsync(500, null, It.IsAny<CancellationToken>(), true, "client-1"))
            .ReturnsAsync([Dispatch("client-1", "Heat.1995.1080p", Now, "imported")]);

        Assert.True(await ImportedDownloads.WasImportedAsync(jobs.Object, "client-1", "Heat.1995.1080p", CancellationToken.None));
        jobs.VerifyAll();
    }

    [Fact]
    public async Task A_queue_that_returns_nothing_at_all_says_not_imported_and_not_sent_again()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(j => j.ListDownloadDispatchesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync((IReadOnlyList<DownloadDispatchItem>)null!);

        Assert.False(await ImportedDownloads.WasImportedAsync(jobs.Object, "client-1", "Heat", CancellationToken.None));
        Assert.False(await ImportedDownloads.SentAgainSinceAsync(jobs.Object, "client-1", "Heat", Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_release_sent_again_after_the_hand_off_that_has_not_been_imported_is_a_new_acquisition()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(j => j.ListDownloadDispatchesAsync(500, null, It.IsAny<CancellationToken>(), true, "client-1"))
            .ReturnsAsync([Dispatch("client-1", "Heat.1995.1080p", Now, null) with { GrabStatus = "succeeded" }]);

        Assert.True(await ImportedDownloads.SentAgainSinceAsync(jobs.Object, "client-1", "Heat.1995.1080p", Now.AddHours(-1), CancellationToken.None));
        Assert.False(await ImportedDownloads.SentAgainSinceAsync(jobs.Object, "client-1", "Heat.1995.1080p", Now.AddHours(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_hand_off_with_no_import_job_has_not_had_its_import_run(string? importJobId)
    {
        var jobs = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        Assert.False(await ImportedDownloads.ImportRanAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Completed, importJobId), CancellationToken.None));
        Assert.False(await ImportedDownloads.FinishedWithItsImportAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Dismissed, importJobId), CancellationToken.None));
    }

    [Fact]
    public async Task A_completed_hand_off_ran_its_import_without_asking_the_job()
    {
        var jobs = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        Assert.True(await ImportedDownloads.ImportRanAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Completed, "job-1"), CancellationToken.None));
        Assert.True(await ImportedDownloads.FinishedWithItsImportAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Completed, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", false)]
    public async Task A_dismissed_hand_off_ran_its_import_only_when_the_import_job_completed(string jobStatus, bool ran)
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(j => j.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(TestJobs.Create("filesystem.import.execute", status: jobStatus));
        var handoff = Handoff(ProcessorHandoffStatuses.Dismissed, "job-1");

        Assert.Equal(ran, await ImportedDownloads.ImportRanAsync(jobs.Object, handoff, CancellationToken.None));
        Assert.Equal(ran, await ImportedDownloads.FinishedWithItsImportAsync(jobs.Object, handoff, CancellationToken.None));
    }

    [Fact]
    public async Task A_dismissed_hand_off_whose_import_job_is_gone_did_not_run_its_import()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(j => j.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync((JobQueueItem?)null);

        Assert.False(await ImportedDownloads.ImportRanAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Dismissed, "job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task A_hand_off_that_is_neither_completed_nor_dismissed_is_not_finished_and_asks_nothing()
    {
        var jobs = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        Assert.False(await ImportedDownloads.FinishedWithItsImportAsync(jobs.Object, Handoff(ProcessorHandoffStatuses.Stalled, "job-1"), CancellationToken.None));
    }
}
