using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1157: a film given a 4K profile in a 1080p library was searched for 1080p,
/// because every search read the library's profile and the title's own choice
/// was saved and then ignored.
///
/// <para>The library's profile is the default. A title that has a profile of its
/// own is searched for - and judged against - that one, and a title that has none
/// keeps using the library's.</para>
/// </summary>
public sealed class ATitlesOwnProfileJudgesItsSearchTests
{
    private readonly List<AcquisitionDecisionRequest> _requests = [];
    private readonly Mock<IAcquisitionDecisionPipeline> _pipeline = new();

    public ATitlesOwnProfileJudgesItsSearchTests()
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
    public async Task A_film_with_a_4K_profile_in_a_1080p_library_is_searched_for_2160p_and_not_1080p()
    {
        await SearchLibraryAsync(titleProfileId: "profile-4k");

        var request = Assert.Single(_requests);
        Assert.Equal(["WEB 2160p", "WEB 1080p"], request.AllowedQualities);
        Assert.DoesNotContain("Bluray 1080p", request.AllowedQualities!);
    }

    [Fact]
    public async Task A_film_without_a_profile_of_its_own_is_searched_for_by_the_librarys_profile()
    {
        await SearchLibraryAsync(titleProfileId: null);

        var request = Assert.Single(_requests);
        Assert.Equal(["WEB 1080p", "Bluray 1080p"], request.AllowedQualities);
    }

    private async Task SearchLibraryAsync(string? titleProfileId)
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([new MovieWantedItem(
                "movie-matrix", "The Matrix", 1999, null, "library-movies", "missing", "not here", false,
                null, "WEB 2160p", false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch)]);
        var mediaState = new Mock<IMediaStateRepository>();
        mediaState
            .Setup(repository => repository.GetTitleQualityProfileIdAsync(MediaKind.Movie, "movie-matrix", It.IsAny<CancellationToken>()))
            .ReturnsAsync(titleProfileId);
        var handler = new MoviesLibrarySearchJobHandler(
            Libraries("library-movies", "movies").Object,
            Profiles().Object,
            Mock.Of<IJobQueueRepository>(),
            movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System,
            mediaStateRepository: mediaState.Object);
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

    private static Mock<IQualityRepository> Profiles()
    {
        var quality = new Mock<IQualityRepository>();
        quality.Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        quality
            .Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QualityProfileItem(
                    "profile-standard", "Standard", "movies", "WEB 1080p", "WEB 1080p,Bluray 1080p", string.Empty,
                    true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new QualityProfileItem(
                    "profile-4k", "Movies / 4K Streaming", "movies", "WEB 2160p", "WEB 2160p,WEB 1080p", string.Empty,
                    true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            ]);
        return quality;
    }
}
