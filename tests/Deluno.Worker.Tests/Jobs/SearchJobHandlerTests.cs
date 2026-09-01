using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class SearchJobHandlerTests
{
    [Fact]
    public async Task EpisodeSearchJobHandler_missing_episode_id_skips_without_touching_any_repository()
    {
        var librariesRepository = new Mock<ILibrariesRepository>(MockBehavior.Strict);
        var qualityRepository = new Mock<IQualityRepository>(MockBehavior.Strict);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>(MockBehavior.Strict);
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);

        var handler = new EpisodeSearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            seriesCatalogRepository.Object,
            jobQueueRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var message = await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished searching for episode.", message);
    }

    [Fact]
    public async Task EpisodeSearchJobHandler_resolves_current_series_numbering_for_automatic_search()
    {
        var librariesRepository = new Mock<ILibrariesRepository>();
        var qualityRepository = new Mock<IQualityRepository>();
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        var jobQueueRepository = new Mock<IJobQueueRepository>();
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>();
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>();
        var activityFeedRepository = new Mock<IActivityFeedRepository>();

        librariesRepository
            .Setup(repository => repository.GetLibraryRoutingAsync("library-tv", It.IsAny<CancellationToken>()))
            .ReturnsAsync((LibraryRoutingSnapshot?)null);
        librariesRepository
            .Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        seriesCatalogRepository
            .Setup(repository => repository.GetEpisodeTargetQualityAsync("episode-1", "library-tv", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        seriesCatalogRepository
            .Setup(repository => repository.GetEpisodeCurrentQualityAsync("episode-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        seriesCatalogRepository
            .Setup(repository => repository.GetEpisodeFilePathAsync("episode-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var episode = new SeriesEpisodeInventoryItem(
            "episode-1",
            1,
            1,
            "Pilot",
            null,
            null,
            Monitored: true,
            HasFile: false,
            WantedStatus: "missing",
            WantedReason: "missing",
            QualityCutoffMet: false,
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            PreventLowerQualityReplacements: false,
            LastQualityDeltaDecision: null,
            LastSearchUtc: null,
            NextEligibleSearchUtc: null,
            UpdatedUtc: DateTimeOffset.UtcNow,
            AbsoluteNumber: 101,
            NumberingSource: SeriesNumberingSources.Provider);
        seriesCatalogRepository
            .Setup(repository => repository.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesInventoryDetail(
                "series-1",
                "Anime Example",
                2024,
                1,
                1,
                0,
                [episode],
                new SeriesNumberingDetail(
                    "series-1",
                    SeriesTypes.Anime,
                    SeriesNumberingSchemes.Absolute,
                    SeriesNumberingSources.Provider,
                    DateTimeOffset.UtcNow,
                    [])));

        AcquisitionDecisionRequest? capturedRequest = null;
        acquisitionPipeline
            .Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionDecisionRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "no sources"),
                Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                "checked",
                "No sources.",
                0,
                0,
                null,
                false,
                null,
                []));
        seriesCatalogRepository
            .Setup(repository => repository.RecordSearchAttemptAsync(
                "series-1",
                "episode-1",
                "library-tv",
                "automatic",
                "checked",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                null,
                null,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        activityFeedRepository
            .Setup(repository => repository.RecordActivityAsync(
                "episode.search.executed",
                It.IsAny<string>(),
                null,
                It.IsAny<string>(),
                "episode",
                "episode-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActivityEventItem)null!);

        var handler = new EpisodeSearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            seriesCatalogRepository.Object,
            jobQueueRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var payload = JsonSerializer.Serialize(new
        {
            episodeId = "episode-1",
            seriesId = "series-1",
            libraryId = "library-tv",
            seasonNumber = 1,
            episodeNumber = 1,
            title = "Pilot"
        });

        await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: payload), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(SeriesNumberingSchemes.Absolute, capturedRequest!.NumberingScheme);
        Assert.Equal(101, capturedRequest.AbsoluteNumber);
        Assert.Null(capturedRequest.AirDate);
        Assert.Null(capturedRequest.SceneSeasonNumber);
        Assert.Null(capturedRequest.SceneEpisodeNumber);
    }

    [Fact]
    public async Task LibrarySearchJobHandler_missing_library_name_skips_without_touching_any_repository()
    {
        var librariesRepository = new Mock<ILibrariesRepository>(MockBehavior.Strict);
        var qualityRepository = new Mock<IQualityRepository>(MockBehavior.Strict);
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);
        var movieCatalogRepository = new Mock<Deluno.Movies.Data.IMovieCatalogRepository>(MockBehavior.Strict);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>(MockBehavior.Strict);
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);

        var handler = new TvLibrarySearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            jobQueueRepository.Object,
            movieCatalogRepository.Object,
            seriesCatalogRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var message = await handler.HandleAsync(TestJobs.Create("library.search", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished checking a library.", message);
    }

    /// <summary>
    /// #303. Per-episode search had three pieces that each worked and never met:
    /// a job type, a handler, and a planner nothing called. A show missing four
    /// scattered episodes was searched for as a *show*, found nothing at the
    /// series level, and the four were never asked for.
    ///
    /// The planning rides on the library search cycle rather than on the
    /// heartbeat's automation lane, so that it inherits the gates the cycle has
    /// already passed — the time-of-day window, the interval, missing versus
    /// upgrade, the manual override and the per-run cap. This asserts the two
    /// halves that would otherwise drift: that it happens at all, and that it is
    /// asked for the same half of the work the cycle itself was.
    /// </summary>
    [Fact]
    public async Task LibrarySearchJobHandler_plans_episode_searches_for_the_episodes_the_series_pass_cannot_reach()
    {
        var librariesRepository = new Mock<ILibrariesRepository>();
        var qualityRepository = new Mock<IQualityRepository>();
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        var jobQueueRepository = new Mock<IJobQueueRepository>();
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>();
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>();
        var activityFeedRepository = new Mock<IActivityFeedRepository>();

        librariesRepository
            .Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // No series-level candidate: the show itself has nothing to grab as a
        // whole, which is exactly the case per-episode search exists for.
        seriesCatalogRepository
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([]);

        seriesCatalogRepository
            .Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                "library-tv", It.IsAny<int>(), It.IsAny<DateTimeOffset>(), false,
                It.IsAny<CancellationToken>(), "missing"))
            .ReturnsAsync([
                new EpisodeSearchEligibilityItem("episode-1", "series-1", 2, 4, "The Drop", null, null),
                // No title yet. Still worth searching for — the plan names it by
                // its code, which is what an indexer query uses anyway.
                new EpisodeSearchEligibilityItem("episode-2", "series-1", 2, 5, null, null, null)
            ]);

        IReadOnlyList<EpisodeSearchPlanItem>? planned = null;
        jobQueueRepository
            .Setup(repository => repository.PlanEpisodeSearchesAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<EpisodeSearchPlanItem>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<EpisodeSearchPlanItem>, CancellationToken>((_, episodes, _) => planned = episodes)
            .Returns(Task.CompletedTask);

        var handler = new TvLibrarySearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            jobQueueRepository.Object,
            new Mock<Deluno.Movies.Data.IMovieCatalogRepository>().Object,
            seriesCatalogRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-tv",
            libraryName = "TV",
            mediaType = "tv",
            checkMissing = true,
            checkUpgrades = false,
            searchKind = "missing",
            maxItems = 25,
            retryDelayHours = 6,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create("library.search", payloadJson: payload), CancellationToken.None);

        jobQueueRepository.Verify(
            repository => repository.PlanEpisodeSearchesAsync("library-tv", It.IsAny<IReadOnlyList<EpisodeSearchPlanItem>>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.NotNull(planned);
        Assert.Equal(["episode-1", "episode-2"], planned!.Select(item => item.EpisodeId));
        Assert.Equal("S02E05", planned[1].Title);
    }
}
