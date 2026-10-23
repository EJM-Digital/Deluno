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
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #599: an automatic search judges by everything the library's profile says,
/// as a person's Search now does: size rules, upgrade stop and acquisition
/// rules included.
///
/// <para><b>Why this file exists.</b> Each search path read the profile one
/// answer at a time, and only the manual one ever learned to read size rules,
/// upgrade stop and acquisition rules. Every scheduled, RSS and episode search
/// left them out.</para>
/// </summary>
public sealed class TheProfileReachesEveryAutomaticSearchTests
{
    private static readonly IReadOnlyList<ProfileSizeRule> SizeRules = [new ProfileSizeRule("WEB 1080p", 4, 20, 0, 0)];
    private static readonly QualityUpgradeStopPolicy UpgradeStop = new(true, false);
    private static readonly ProfileScoreLimits ScoreLimits = new(MinFormatScore: 25, CutoffFormatScore: 100, MinUpgradeFormatScore: 10);

    private readonly List<AcquisitionDecisionRequest> _requests = [];
    private readonly Mock<IAcquisitionDecisionPipeline> _pipeline = new();

    public TheProfileReachesEveryAutomaticSearchTests()
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
    public async Task A_scheduled_film_search_carries_the_profiles_size_rules_upgrade_stop_and_score_limits()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([new MovieWantedItem(
                "movie-cleaner", "Cleaner", 2007, null, "library-movies", "upgrade", "below target", true,
                "WEB 1080p", "WEB 1080p", false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch)]);
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
        Assert.Equal(SizeRules, request.SizeRules);
        Assert.Equal(UpgradeStop, request.UpgradeStop);
        Assert.Equal(ScoreLimits, request.ScoreLimits);
        Assert.Equal(["WEB 1080p", "Bluray 1080p"], request.AllowedQualities);
    }

    [Fact]
    public async Task An_episode_search_carries_the_profiles_size_rules_upgrade_stop_and_score_limits()
    {
        var handler = new EpisodeSearchJobHandler(
            Libraries("library-tv", "tv").Object,
            Profile().Object,
            Mock.Of<ISeriesCatalogRepository>(),
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
        Assert.Equal(SizeRules, request.SizeRules);
        Assert.Equal(UpgradeStop, request.UpgradeStop);
        Assert.Equal(ScoreLimits, request.ScoreLimits);
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
                true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                SizeRules: SizeRules,
                UpgradeStop: UpgradeStop,
                ScoreLimits: ScoreLimits)]);
        return quality;
    }
}
