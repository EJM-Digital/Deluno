using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #497: a processor that could not use a file tells Deluno what it did with
/// it. When it deleted the download, it counts the deletion as safe only if
/// Deluno says yes, so yes has to mean the release really is refused.
/// </summary>
public sealed class ProcessorFailureReportHandlerTests
{
    private readonly Mock<IJobQueueRepository> _jobQueue = new();
    private readonly Mock<IDownloadDispatchesRepository> _dispatches = new();
    private readonly Mock<IBlockedReleaseRepository> _blocked = new();
    private readonly Mock<IRefusedDownloadCleanupService> _cleanup = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();

    [Fact]
    public async Task A_rejected_download_is_refused_forgotten_and_searched_for_again()
    {
        GivenTheDispatch();
        BlockedRelease? refused = null;
        _blocked.Setup(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
            .Callback<BlockedRelease, CancellationToken>((release, _) => refused = release)
            .ReturnsAsync((BlockedRelease release, CancellationToken _) => release with { Id = "blocked-1" });
        EnqueueJobRequest? search = null;
        _scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => search = request)
            .ReturnsAsync(Job("search-1"));

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.True(outcome.Acted);
        Assert.Equal("blocked-1", outcome.BlockedReleaseId);
        Assert.Equal("search-1", outcome.SearchJobId);

        Assert.NotNull(refused);
        Assert.Equal(ImportFailurePolicy.ProcessorRejected, refused!.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(BlockedReleaseKeys.For("Sintel.2010.1080p", "Soak Torznab"), refused.ReleaseKey);
        Assert.Contains("guardrail", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("No usable audio track", refused.Reason, StringComparison.Ordinal);
        Assert.True(ImportFailurePolicy.ShouldDeletePayload(refused.ReasonCode));

        _cleanup.Verify(service => service.CleanUpOneAsync("blocked-1", It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(search);
        Assert.Equal(LibrarySearchJobTypes.Movies, search!.JobType);
        Assert.Equal("processor-rejected:handoff-1", search.IdempotencyKey);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        Assert.Equal("movie-1", payload.RootElement.GetProperty("targetEntityId").GetString());
    }

    [Fact]
    public async Task Without_the_download_it_came_from_nothing_is_refused_and_the_answer_is_no()
    {
        _jobQueue.Setup(repo => repo.FindRecentDispatchLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DispatchCatalogueLink?)null);

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.False(outcome.Acted);
        Assert.Contains("hand it back", outcome.Message, StringComparison.Ordinal);
        _blocked.Verify(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        _scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_refusal_that_could_not_be_recorded_is_answered_no_and_searches_nothing()
    {
        GivenTheDispatch();
        _blocked.Setup(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.False(outcome.Acted);
        _scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The refusal is what the processor is waiting on. A client that is off
    /// right now is told again by the scheduled clean-up.
    /// </summary>
    [Fact]
    public async Task A_client_that_cannot_be_told_yet_does_not_undo_the_refusal()
    {
        GivenTheDispatch();
        _blocked.Setup(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BlockedRelease release, CancellationToken _) => release with { Id = "blocked-1" });
        _cleanup.Setup(service => service.CleanUpOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        _scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Job("search-1"));

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.True(outcome.Acted);
    }

    [Fact]
    public async Task A_held_file_becomes_a_recovery_case_and_refuses_nothing()
    {
        CreateSeriesImportRecoveryCaseRequest? raised = null;
        _series.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateSeriesImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateSeriesImportRecoveryCaseRequest, CancellationToken>((request, _) => raised = request)
            .ReturnsAsync((SeriesImportRecoveryCase)null!);

        await Handler().HandleHeldAsync(Report("tv"), CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal("processor-held", raised!.FailureKind);
        Assert.Contains("keeping the original", raised.Summary, StringComparison.Ordinal);
        _movies.Verify(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _blocked.Verify(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ProcessorFailureReportHandler Handler() => new(
        _jobQueue.Object,
        _dispatches.Object,
        _blocked.Object,
        _cleanup.Object,
        _scheduler.Object,
        _movies.Object,
        _series.Object,
        NullLogger<ProcessorFailureReportHandler>.Instance);

    private void GivenTheDispatch()
    {
        _jobQueue.Setup(repo => repo.FindRecentDispatchLinkAsync("qbittorrent", "Sintel.2010.1080p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-1", "movie", "movie-1"));
        var dispatch = (DownloadDispatchItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem));
        _dispatches.Setup(repo => repo.GetDispatchAsync("dispatch-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dispatch with
            {
                Id = "dispatch-1",
                LibraryId = "library-movies",
                MediaType = "movies",
                EntityType = "movie",
                EntityId = "movie-1",
                ReleaseName = "Sintel.2010.1080p",
                IndexerName = "Soak Torznab",
                DownloadClientId = "qbittorrent",
                DownloadClientName = "qBittorrent",
                TorrentHashOrItemId = "hash-1"
            });
    }

    private static ProcessorFailureReport Report(string mediaType)
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return new ProcessorFailureReport(
            library with { Id = "library-movies", Name = "Movies", MediaType = mediaType },
            new ProcessorHandoffItem(
                "handoff-1", "library-movies", mediaType, "qbittorrent", "hash-1", "Sintel.2010.1080p",
                @"D:\Downloads\Sintel.2010.1080p", "Weir", "submitted", null, null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            "Weir",
            "No usable audio track",
            "guardrail");
    }

    private static JobQueueItem Job(string id)
    {
        var job = (JobQueueItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem));
        return job with { Id = id };
    }
}
