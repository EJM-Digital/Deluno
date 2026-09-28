using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Quality;
using Deluno.Quality.Data;
using Moq;

namespace Deluno.Movies.Tests.Services;

/// <summary>#1046: a collection can give tags to every movie it adds.</summary>
public sealed class MovieCollectionTagTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static LibraryItem Library()
        => new(
            Id: "library-movies", Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "D:\\Media\\Movies",
            DownloadsPath: null, QualityProfileId: null, QualityProfileName: null, CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true, UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null,
            ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true,
            MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12,
            MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active",
            SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: Stamp, UpdatedUtc: Stamp);

    private static MovieCollectionItem Collection(string tags)
        => new(
            "collection-1", "tmdb", "645", "Nolan Collection", null, null, null, "library-movies", "Movies", "D:\\Media\\Movies",
            Monitored: true, MonitorMovies: true, QualityProfileId: null, QualityProfileName: null, MinimumAvailability: "released",
            SearchOnAdd: false, MemberCount: 2, HeldCount: 0, MissingCount: 2, LastSyncedUtc: null, NextSyncUtc: null, LastSyncError: null,
            CreatedUtc: Stamp, UpdatedUtc: Stamp, Tags: tags);

    private static MovieCollectionMemberItem Member(string providerId, string title)
        => new("collection-1", providerId, title, 2010, null, null, null, null, null, null, false, Stamp);

    private static MovieListItem Movie(string id, string title)
        => new(id, title, 2010, null, true, false, "tmdb", "1", null, null, null, null, null, [], null, null, null, null, Stamp, Stamp);

    private static (MovieCollectionService Service, Mock<ITitleTagService> Tags, List<string> Order, Mock<IActivityFeedRepository> Activity) Build(string tags)
    {
        var collections = new Mock<IMovieCollectionsRepository>();
        collections.Setup(repo => repo.GetAsync("collection-1", It.IsAny<CancellationToken>())).ReturnsAsync(Collection(tags));
        collections.Setup(repo => repo.ListMembersAsync("collection-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Member("1", "Brand new film"), Member("2", "Film already here")]);
        collections.Setup(repo => repo.LinkMovieAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(provider => provider.GetMovieCollectionAsync("645", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataCollection("tmdb", "645", "Nolan Collection", null, null, null, []));

        var order = new List<string>();
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.FindExistingIdAsync("Brand new film", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        movies.Setup(repo => repo.FindExistingIdAsync("Film already here", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("movie-old");
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Movie("movie-new", "Brand new film"));
        movies.Setup(repo => repo.GetByIdAsync("movie-old", It.IsAny<CancellationToken>())).ReturnsAsync(Movie("movie-old", "Film already here"));
        movies.Setup(repo => repo.EnsureWantedStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wanted")).Returns(Task.CompletedTask);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var decisions = new Mock<IMediaDecisionService>();
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        var titleTags = new Mock<ITitleTagService>();
        titleTags.Setup(service => service.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("tagged")).ReturnsAsync(["franchise"]);
        var activity = new Mock<IActivityFeedRepository>();

        var service = new MovieCollectionService(
            collections.Object, metadata.Object, movies.Object, libraries.Object, new Mock<IQualityRepository>().Object,
            decisions.Object, new Mock<IJobQueueRepository>().Object, new FixedClock(Stamp), titleTags.Object, activity.Object);
        return (service, titleTags, order, activity);
    }

    [Fact]
    public async Task A_movie_the_collection_adds_gets_the_collections_tags_before_its_wanted_state_and_a_movie_already_held_does_not()
    {
        var (service, tags, order, activity) = Build("franchise, kids");

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        tags.Verify(t => t.GiveAsync(MediaKind.Movie, "movie-new",
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "franchise", "kids" })), It.IsAny<CancellationToken>()), Times.Once);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), "movie-old", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("tagged", order[0]);
        activity.Verify(a => a.RecordActivityAsync("collection.tagged", It.Is<string>(m => m.Contains("franchise, kids")),
            It.IsAny<string?>(), It.IsAny<string?>(), "movie-collection", "collection-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_collection_with_no_tags_gives_none()
    {
        var (service, tags, _, activity) = Build("");

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(a => a.RecordActivityAsync("collection.tagged", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
