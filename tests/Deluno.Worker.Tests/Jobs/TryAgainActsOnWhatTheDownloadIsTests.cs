using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// Try again on a recovery case acts on what its download is now (#558).
///
/// <para>The catalogues keep one kind, "downloadFailed", for a download the client
/// never showed and for one it showed that was never imported, so the download
/// itself decides: never shown, search again; arrived, go back to the processor
/// that gave up on it. Where nothing can be done yet the answer says why rather
/// than acting.</para>
/// </summary>
public sealed class TryAgainActsOnWhatTheDownloadIsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_download_Weir_still_holds_is_left_with_Weir()
    {
        var world = new World(detected: true, handoffStatus: ProcessorHandoffStatuses.Submitted);

        var outcome = await world.RetryAsync("downloadFailed");

        Assert.False(outcome.Retried);
        Assert.Contains("Weir still has", outcome.Message);
        world.Processors.Verify(repository => repository.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, null, It.IsAny<CancellationToken>()), Times.Never);
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_download_the_processor_gave_up_on_goes_back_to_it()
    {
        var world = new World(detected: true, handoffStatus: ProcessorHandoffStatuses.Failed);

        var outcome = await world.RetryAsync("downloadFailed");

        Assert.True(outcome.Retried);
        Assert.Contains("hand", outcome.Message);
        world.Processors.Verify(repository => repository.UpdateProcessorHandoffAsync("handoff-1", ProcessorHandoffStatuses.Waiting, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
        // It arrived, so fetching it again would only make a second copy.
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_download_that_arrived_with_nothing_waiting_on_it_is_not_fetched_again()
    {
        var world = new World(detected: true, handoffStatus: null);

        var outcome = await world.RetryAsync("downloadFailed");

        Assert.False(outcome.Retried);
        Assert.Contains("Import it from its row in Transfers", outcome.Message);
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_download_the_client_never_showed_is_ended_and_searched_for_again()
    {
        var world = new World(detected: false, handoffStatus: null);

        var outcome = await world.RetryAsync("downloadFailed");

        Assert.True(outcome.Retried);
        world.Dispatches.Verify(repository => repository.ArchiveDispatchAsync("d1", "retried", It.IsAny<CancellationToken>()), Times.Once);
        world.MediaState.Verify(state => state.SetDownloadingAsync(MediaKind.Movie, "movie-1", false, Now, It.IsAny<CancellationToken>()), Times.Once);
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == LibrarySearchJobTypes.For("movies") && request.PayloadJson!.Contains("\"targetEntityId\":\"movie-1\"")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("imported", true, "imported after all")]
    [InlineData("pending", false, "still queued or running")]
    [InlineData("failed", false, "no longer has a failed import")]
    public async Task An_import_with_nothing_failed_to_run_again_says_where_it_stands(string importStatus, bool retried, string says)
    {
        var world = new World(detected: true, handoffStatus: null, importStatus: importStatus);

        var outcome = await world.RetryAsync("importFailed");

        Assert.Equal(retried, outcome.Retried);
        Assert.Contains(says, outcome.Message);
    }

    [Fact]
    public async Task A_failed_import_is_run_again()
    {
        var world = new World(detected: true, handoffStatus: null, importStatus: "failed", failedImports: 1);

        var outcome = await world.RetryAsync("importFailed");

        Assert.True(outcome.Retried);
        world.Jobs.Verify(repository => repository.RetryFailedImportAsync("d1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("downloadFailed", true)]
    [InlineData("importFailed", true)]
    [InlineData("vanished-downloading", true)]
    [InlineData("quality", false)]
    [InlineData("unmatched", false)]
    [InlineData("corrupt", false)]
    public void Try_again_is_offered_only_where_it_can_act(string kind, bool offered)
        => Assert.Equal(offered, RecoveryCaseRetry.CanRetry(kind));

    /// <summary>
    /// #927: a vanished download always searches again, whatever DetectedUtc
    /// says - it was already detected downloading, but never handed to a
    /// processor, so the "downloadFailed" hand-over-again branch does not apply.
    /// </summary>
    [Fact]
    public async Task Search_for_another_on_a_vanished_download_ends_it_and_searches_again_even_though_it_was_detected()
    {
        var world = new World(detected: true, handoffStatus: null);

        var outcome = await world.RetryAsync("vanished-downloading");

        Assert.True(outcome.Retried);
        world.Dispatches.Verify(repository => repository.ArchiveDispatchAsync("d1", "retried", It.IsAny<CancellationToken>()), Times.Once);
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == LibrarySearchJobTypes.For("movies") && request.PayloadJson!.Contains("\"targetEntityId\":\"movie-1\"")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_wanting_it_ends_the_download_and_unmonitors_exactly_the_title_named()
    {
        var world = new World(detected: true, handoffStatus: null);
        var unmonitor = new Mock<IDispatchTitleUnmonitorService>();
        unmonitor.Setup(service => service.CanHandle("movie")).Returns(true);
        unmonitor.Setup(service => service.UnmonitorAsync("movie", "movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var outcome = await RecoveryCaseRetry.StopWantingAsync(
            JsonSerializer.Serialize(new { DispatchId = "d1" }),
            world.Dispatches.Object,
            unmonitor.Object,
            CancellationToken.None);

        Assert.True(outcome.Retried);
        Assert.Contains("stopped wanting", outcome.Message, StringComparison.OrdinalIgnoreCase);
        world.Dispatches.Verify(repository => repository.ArchiveDispatchAsync("d1", "stopped-wanting", It.IsAny<CancellationToken>()), Times.Once);
        unmonitor.Verify(service => service.UnmonitorAsync("movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_wanting_it_is_refused_when_nothing_can_unmonitor_this_entity_type()
    {
        var world = new World(detected: true, handoffStatus: null);
        var unmonitor = new Mock<IDispatchTitleUnmonitorService>();
        unmonitor.Setup(service => service.CanHandle("movie")).Returns(false);

        var outcome = await RecoveryCaseRetry.StopWantingAsync(
            JsonSerializer.Serialize(new { DispatchId = "d1" }),
            world.Dispatches.Object,
            unmonitor.Object,
            CancellationToken.None);

        Assert.False(outcome.Retried);
        unmonitor.Verify(service => service.UnmonitorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Dispatches.Verify(repository => repository.ArchiveDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class World
    {
        public Mock<IDownloadDispatchesRepository> Dispatches { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();
        public Mock<IMediaStateRepository> MediaState { get; } = new();
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IProcessorRepository> Processors { get; } = new();

        public World(bool detected, string? handoffStatus, string? importStatus = null, int failedImports = 0)
        {
            var dispatch = new DownloadDispatchItem(
                Id: "d1", LibraryId: "library-1", MediaType: "movies", EntityType: "movie", EntityId: "movie-1",
                ReleaseName: Release, IndexerName: "Fixture", DownloadClientId: "client-1", DownloadClientName: "SABnzbd",
                Status: detected ? "sent" : "failed", NotesJson: null, CreatedUtc: Now, GrabStatus: "succeeded", GrabAttemptedUtc: Now,
                GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: detected ? null : "not_found", GrabResponseJson: null,
                DetectedUtc: detected ? Now : null, TorrentHashOrItemId: "nzo-1", DownloadedBytes: null, ImportStatus: importStatus,
                ImportDetectedUtc: null, ImportCompletedUtc: null, ImportedFilePath: null, ImportFailureCode: null,
                ImportFailureMessage: null, CircuitOpenUntilUtc: null);
            Dispatches.Setup(repository => repository.GetDispatchIncludingArchivedAsync("d1", It.IsAny<CancellationToken>())).ReturnsAsync(dispatch);
            Jobs.Setup(repository => repository.RetryFailedImportAsync("d1", It.IsAny<CancellationToken>())).ReturnsAsync(failedImports);
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new Deluno.Libraries.Contracts.LibraryItem(
                    Id: "library-1", Name: "Movies", MediaType: "movies", Purpose: "library", RootPath: "C:\\Movies",
                    DownloadsPath: "C:\\Downloads", QualityProfileId: null, QualityProfileName: null, CutoffQuality: null,
                    UpgradeUntilCutoff: false, UpgradeUnknownItems: false, ImportWorkflow: "refine-before-import",
                    ProcessorName: "Weir", ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "block",
                    AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 12,
                    RetryDelayHours: 24, MaxItemsPerRun: 25, SearchWindowStartHour: null, SearchWindowEndHour: null,
                    AutomationStatus: "idle", SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null,
                    CreatedUtc: Now, UpdatedUtc: Now)]);
            Processors.Setup(repository => repository.ListProcessorHandoffsAsync("library-1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(handoffStatus is null
                    ? []
                    : [new ProcessorHandoffItem("handoff-1", "library-1", "movies", "client-1", "nzo-1", Release, "C:\\Downloads\\Movies\\x", "Weir", handoffStatus, null, null, null, Now, Now)]);
            Processors.Setup(repository => repository.FindProcessorConnectionAsync(It.IsAny<string?>(), "Weir", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessorConnectionItem("connection-1", "Weir", "weir", "http://weir.invalid", "X-Api-Key", null, true, "healthy", null, null, Now, Now));
        }

        public Task<RecoveryRetryOutcome> RetryAsync(string kind)
            => RecoveryCaseRetry.RetryAsync(
                kind,
                JsonSerializer.Serialize(new { DispatchId = "d1" }),
                Dispatches.Object,
                Jobs.Object,
                Scheduler.Object,
                MediaState.Object,
                Libraries.Object,
                Processors.Object,
                new FixedClock(Now),
                CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
