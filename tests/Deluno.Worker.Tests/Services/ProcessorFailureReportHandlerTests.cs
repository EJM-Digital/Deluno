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

    public ProcessorFailureReportHandlerTests()
        // Nothing refused for this title yet, which is what the cap on repeated
        // refusals reads (#576).
        => _blocked.Setup(repo => repo.ListForAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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

        // The grab is closed, so nothing reads it as still downloading (#497, rig).
        _dispatches.Verify(repo => repo.ArchiveDispatchAsync(It.IsAny<string>(), It.Is<string>(reason => reason.Contains("Rejected by", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);

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

    /// <summary>
    /// #576. A rejection puts the release on the blocklist and starts a search
    /// for another copy. A processor that refuses everything therefore walks
    /// the whole result list — reject, block, search, grab, download, submit,
    /// reject — with nothing counting the cycles and every one of them costing
    /// a full download. Over a fortnight that is a slow, unbounded grab storm.
    /// </summary>
    [Fact]
    public async Task A_title_a_processor_keeps_refusing_is_not_searched_for_again()
    {
        GivenTheDispatch();
        _blocked.Setup(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BlockedRelease release, CancellationToken _) => release with { Id = "blocked-6" });
        _blocked.Setup(repo => repo.ListForAsync("movies", "movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(0, 5).Select(index => Refused($"blocked-{index}"))]);
        CreateMovieImportRecoveryCaseRequest? raised = null;
        _movies.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => raised = request)
            .ReturnsAsync((MovieImportRecoveryCase)null!);

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.True(outcome.Acted);
        Assert.Null(outcome.SearchJobId);
        Assert.Contains("refused 5 copies", outcome.Message);
        _scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        // And a person is told, so the title does not simply go quiet.
        Assert.NotNull(raised);
        Assert.Equal("processor-refuses-every-copy", raised!.FailureKind);
    }

    /// <summary>A bad release or two must not stop the title arriving.</summary>
    [Fact]
    public async Task A_title_refused_a_few_times_is_still_searched_for()
    {
        GivenTheDispatch();
        _blocked.Setup(repo => repo.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BlockedRelease release, CancellationToken _) => release with { Id = "blocked-3" });
        _blocked.Setup(repo => repo.ListForAsync("movies", "movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(0, 2).Select(index => Refused($"blocked-{index}"))]);
        _scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Job("search-1"));

        var outcome = await Handler().HandleRejectedAsync(Report("movies"), CancellationToken.None);

        Assert.Equal("search-1", outcome.SearchJobId);
    }

    private static BlockedRelease Refused(string id)
        => ((BlockedRelease)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BlockedRelease))) with
        {
            Id = id,
            ReasonCode = ImportFailurePolicy.ProcessorRejected,
            State = BlockedReleaseStates.Refused,
            MediaType = "movies",
            EntityId = "movie-1"
        };

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

    private static LibraryItem TvLibrary()
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return library with { Id = "library-tv", Name = "TV", MediaType = "tv" };
    }

    private static SeriesEpisodeInventoryItem Episode(string episodeId, int season, int episode)
    {
        var item = (SeriesEpisodeInventoryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SeriesEpisodeInventoryItem));
        return item with { EpisodeId = episodeId, SeasonNumber = season, EpisodeNumber = episode };
    }

    private static SeriesInventoryDetail SeriesDetail(string title, params SeriesEpisodeInventoryItem[] episodes)
    {
        var detail = (SeriesInventoryDetail)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SeriesInventoryDetail));
        return detail with { Title = title, Episodes = episodes };
    }

    /// <summary>
    /// #930: a missing output file's own name resolves to a canonical episode,
    /// so the case names that episode rather than only the filename.
    /// </summary>
    [Fact]
    public async Task A_missing_file_that_names_an_episode_is_reported_against_that_episode()
    {
        _series.Setup(repo => repo.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SeriesDetail("Lioness", Episode("episode-7", 2, 7)));
        CreateSeriesImportRecoveryCaseRequest? raised = null;
        _series.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateSeriesImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateSeriesImportRecoveryCaseRequest, CancellationToken>((request, _) => raised = request)
            .ReturnsAsync((SeriesImportRecoveryCase)null!);

        await Handler().HandleOutputsMissingAsync(
            new ProcessorMissingOutputReport(
                TvLibrary(), "series-1", "Weir", "Lioness.S02.1080p", @"D:\Processed\Lioness.S02",
                [@"D:\Processed\Lioness.S02\Lioness.S02E07.1080p.mkv"], 10),
            CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal("processor-output-missing", raised!.FailureKind);
        Assert.Equal("Lioness S02: 9 of 10 imported, E07 missing from Weir's output.", raised.Summary);
        Assert.Contains("Search for this episode", raised.RecommendedAction);
        using var details = JsonDocument.Parse(raised.DetailsJson!);
        Assert.Equal("episode-7", details.RootElement.GetProperty("episodeId").GetString());
    }

    /// <summary>A movie has no episode to resolve against, so the case names the file instead.</summary>
    [Fact]
    public async Task A_missing_file_for_a_movie_is_reported_by_name()
    {
        CreateMovieImportRecoveryCaseRequest? raised = null;
        _movies.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => raised = request)
            .ReturnsAsync((MovieImportRecoveryCase)null!);
        var movieLibrary = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));

        await Handler().HandleOutputsMissingAsync(
            new ProcessorMissingOutputReport(
                movieLibrary with { Id = "library-movies", Name = "Movies", MediaType = "movies" },
                "movie-1", "Weir", "Sintel.2010.1080p", @"D:\Processed\Sintel",
                [@"D:\Processed\Sintel\Sintel.2010.1080p.mkv"], 1),
            CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal("processor-output-missing", raised!.FailureKind);
        Assert.Contains("Sintel.2010.1080p.mkv", raised.Summary);
        _series.Verify(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateSeriesImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
