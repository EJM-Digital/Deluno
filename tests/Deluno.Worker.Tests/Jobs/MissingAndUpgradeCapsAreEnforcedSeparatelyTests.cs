using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #830: a library's missing-item cap and its upgrade-item cap are enforced
/// independently against a combined candidate list, so a large missing
/// backlog can never crowd every upgrade candidate out of a cycle (or the
/// other way round).
/// </summary>
public sealed class MissingAndUpgradeCapsAreEnforcedSeparatelyTests
{
    [Fact]
    public async Task A_combined_cycle_takes_up_to_each_kinds_own_cap_not_a_shared_one()
    {
        var requests = new List<string> { };
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(p => p.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionDecisionRequest, CancellationToken>((request, _) => requests.Add(request.Title))
            .ReturnsAsync(new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "Nothing found."),
                Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                "checked",
                "Nothing found.",
                1,
                1,
                null,
                false,
                null,
                []));

        // Three missing candidates and three upgrade candidates in one
        // combined list, exactly as the repository returns them when both
        // kinds are due in the same cycle.
        var candidates = new[]
        {
            Movie("movie-missing-1", "Missing One", "missing"),
            Movie("movie-missing-2", "Missing Two", "missing"),
            Movie("movie-missing-3", "Missing Three", "missing"),
            Movie("movie-upgrade-1", "Upgrade One", "upgrade"),
            Movie("movie-upgrade-2", "Upgrade Two", "upgrade"),
            Movie("movie-upgrade-3", "Upgrade Three", "upgrade")
        };

        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync(candidates);

        var handler = new MoviesLibrarySearchJobHandler(
            Libraries("library-movies", "movies").Object,
            Profile().Object,
            Mock.Of<IJobQueueRepository>(),
            movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);

        // Both kinds due, unscoped: exactly the combined-cycle shape #830
        // targets. One cap of 1 for each kind should let exactly one missing
        // and one upgrade candidate through, however many of each exist.
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-movies",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 2,
            maxMissingItems = 1,
            maxUpgradeItems = 1,
            retryDelayHours = 1,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);

        Assert.Equal(["Missing One", "Upgrade One"], requests);
    }

    [Theory]
    [InlineData(true, false, "missing", 2)]
    [InlineData(false, true, "upgrade", 3)]
    public async Task A_search_of_one_kind_asks_only_for_that_kind_and_no_more_than_its_own_cap(bool checkMissing, bool checkUpgrades, string expectedKind, int expectedCap)
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync([]);
        var handler = new MoviesLibrarySearchJobHandler(
            Libraries("library-movies", "movies").Object,
            Profile().Object,
            Mock.Of<IJobQueueRepository>(),
            movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            Mock.Of<IAcquisitionDecisionPipeline>(),
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-movies",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing,
            checkUpgrades,
            maxItems = 10,
            maxMissingItems = 2,
            maxUpgradeItems = 3,
            retryDelayHours = 1,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);

        movies.Verify(repository => repository.ListEligibleWantedAsync(
            "library-movies", expectedCap, It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), expectedKind, It.IsAny<CatalogueFilters?>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
    }

    private static MovieWantedItem Movie(string id, string title, string wantedStatus)
        => new(
            id, title, 2020, null, "library-movies", wantedStatus, "not found", false,
            null, "WEB 1080p", false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch);

    private static Mock<ILibrariesRepository> Libraries(string id, string mediaType)
    {
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with
        {
            Id = id,
            Name = mediaType,
            MediaType = mediaType,
            QualityProfileId = "profile-standard",
            RetryDelayHours = 1
        };
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        return libraries;
    }

    private static Mock<IQualityRepository> Profile()
    {
        var quality = new Mock<IQualityRepository>();
        quality.Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        quality
            .Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Deluno.Quality.Contracts.QualityProfileItem(
                "profile-standard", "Standard", "movies", "WEB 1080p", "WEB 1080p,Bluray 1080p", string.Empty,
                true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
        return quality;
    }
}
