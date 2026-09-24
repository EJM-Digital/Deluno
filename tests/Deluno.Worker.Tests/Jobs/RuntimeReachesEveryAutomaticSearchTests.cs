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
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #630 scaled Size Rules against a title's real runtime, but only wired it
/// through interactive search and grab - MovieWantedItem and SeriesWantedItem
/// were thinner than the catalogue items they come from, the same defect
/// class MediaEntryDetails' own doc comment already names. #638 closes that
/// gap for the scheduled/automatic path.
/// </summary>
public sealed class RuntimeReachesEveryAutomaticSearchTests
{
    private readonly List<AcquisitionDecisionRequest> _requests = [];
    private readonly Mock<IAcquisitionDecisionPipeline> _pipeline = new();

    public RuntimeReachesEveryAutomaticSearchTests()
        => _pipeline
            .Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionDecisionRequest, CancellationToken>((request, _) => _requests.Add(request))
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

    [Fact]
    public async Task A_scheduled_film_search_carries_the_films_own_runtime()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([new MovieWantedItem(
                "movie-tears", "Tears of Steel", 2012, null, "library-movies", "missing", "not found", false,
                null, "WEB 1080p", false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch,
                RuntimeMinutes: 12)]);
        var handler = new MoviesLibrarySearchJobHandler(
            Libraries("library-movies", "movies").Object,
            Profile().Object,
            Mock.Of<IJobQueueRepository>(),
            movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-movies",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 5,
            retryDelayHours = 1,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);

        var request = Assert.Single(_requests);
        Assert.Equal(12, request.RuntimeMinutes);
    }

    [Fact]
    public async Task A_scheduled_show_level_search_carries_the_shows_own_runtime()
    {
        var series = new Mock<ISeriesCatalogRepository>();
        series
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([new SeriesWantedItem(
                SeriesId: "series-pioneer",
                Title: "Pioneer One",
                StartYear: 2010,
                ImdbId: null,
                LibraryId: "library-tv",
                WantedStatus: "missing",
                WantedReason: "not found",
                HasFile: false,
                CurrentQuality: null,
                TargetQuality: "WEB 1080p",
                QualityCutoffMet: false,
                PreventLowerQualityReplacements: false,
                LastQualityDeltaDecision: null,
                MissingSinceUtc: null,
                LastSearchUtc: null,
                NextEligibleSearchUtc: null,
                LastSearchResult: null,
                UpdatedUtc: DateTimeOffset.UnixEpoch,
                RuntimeMinutes: 45)]);
        series
            .Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([]);
        var handler = new TvLibrarySearchJobHandler(
            Libraries("library-tv", "tv").Object,
            Profile().Object,
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IMovieCatalogRepository>(),
            series.Object,
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-tv",
            libraryName = "TV",
            mediaType = "tv",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 5,
            retryDelayHours = 1,
            triggeredBy = "schedule"
        });

        await handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("tv"), payloadJson: payload), CancellationToken.None);

        var request = Assert.Single(_requests);
        Assert.Equal(45, request.RuntimeMinutes);
    }

    [Fact]
    public async Task An_episode_search_carries_the_shows_runtime_not_a_per_episode_one()
    {
        var series = new Mock<ISeriesCatalogRepository>();
        series
            .Setup(repository => repository.GetByIdAsync("series-bb", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem)) with
            {
                Id = "series-bb",
                Title = "Breaking Bad",
                RuntimeMinutes = 47
            });
        var handler = new EpisodeSearchJobHandler(
            Libraries("library-tv", "tv").Object,
            Profile().Object,
            series.Object,
            Mock.Of<IJobQueueRepository>(),
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);
        var payload = JsonSerializer.Serialize(new
        {
            episodeId = "e-201",
            seriesId = "series-bb",
            libraryId = "library-tv",
            seasonNumber = 2,
            episodeNumber = 1,
            title = "Seven Thirty-Seven"
        });

        await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: payload), CancellationToken.None);

        var request = Assert.Single(_requests);
        Assert.Equal(47, request.RuntimeMinutes);
    }

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
            .ReturnsAsync([new QualityProfileItem(
                "profile-standard", "Standard", "movies", "WEB 1080p", "WEB 1080p,Bluray 1080p", string.Empty,
                true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
        return quality;
    }
}
