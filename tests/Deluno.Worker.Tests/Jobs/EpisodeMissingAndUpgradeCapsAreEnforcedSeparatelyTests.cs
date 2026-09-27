using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #830: the episode-level pass (<c>FetchEligibleEpisodesAsync</c>) shared one
/// combined <c>MaxItemsPerRun</c> across missing and upgrade episodes even
/// after the show-level and movie-level passes were split. This proves the
/// same per-kind cap now applies at the episode level too.
/// </summary>
public sealed class EpisodeMissingAndUpgradeCapsAreEnforcedSeparatelyTests
{
    [Fact]
    public async Task An_episode_cycle_takes_up_to_each_kinds_own_cap_not_a_shared_one()
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

        // No series-level candidate: this test is only about the episode pass.
        seriesCatalogRepository
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<Deluno.Contracts.CatalogueFilters?>(), It.IsAny<bool>()))
            .ReturnsAsync([]);

        // Three missing episodes (across three different series/seasons, so
        // none can group into a season pack) and three upgrade episodes, in
        // one combined list -- exactly what the repository returns when both
        // kinds are due in the same, unscoped cycle.
        seriesCatalogRepository
            .Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                "library-tv", It.IsAny<int>(), It.IsAny<DateTimeOffset>(), false,
                It.IsAny<CancellationToken>(), null, It.IsAny<Deluno.Contracts.CatalogueFilters?>(), It.IsAny<bool>()))
            .ReturnsAsync([
                new EpisodeSearchEligibilityItem("ep-missing-1", "series-missing-1", 1, 1, "Missing One", null, null, "missing"),
                new EpisodeSearchEligibilityItem("ep-missing-2", "series-missing-2", 1, 1, "Missing Two", null, null, "missing"),
                new EpisodeSearchEligibilityItem("ep-missing-3", "series-missing-3", 1, 1, "Missing Three", null, null, "missing"),
                new EpisodeSearchEligibilityItem("ep-upgrade-1", "series-upgrade-1", 1, 1, "Upgrade One", null, null, "upgrade"),
                new EpisodeSearchEligibilityItem("ep-upgrade-2", "series-upgrade-2", 1, 1, "Upgrade Two", null, null, "upgrade"),
                new EpisodeSearchEligibilityItem("ep-upgrade-3", "series-upgrade-3", 1, 1, "Upgrade Three", null, null, "upgrade")
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

        // Both kinds due, unscoped, with one cap of 1 for each kind: exactly
        // one missing and one upgrade episode should be planned, however many
        // of each the repository handed back.
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-tv",
            libraryName = "TV",
            mediaType = "tv",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 2,
            maxMissingItems = 1,
            maxUpgradeItems = 1,
            retryDelayHours = 1,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create("library.search", payloadJson: payload), CancellationToken.None);

        Assert.NotNull(planned);
        Assert.Equal(["ep-missing-1", "ep-upgrade-1"], planned!.Select(item => item.EpisodeId));
    }
}
