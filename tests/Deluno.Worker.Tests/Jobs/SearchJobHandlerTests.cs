using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Jobs.Contracts;
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

        var handler = new LibrarySearchJobHandler(
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

        var handler = new LibrarySearchJobHandler(
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
