using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
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

/// <summary>
/// A film excluded from a followed collection is never added, never made
/// wanted and never searched for, however the collection is set up. The
/// Collections page offers Exclude and Include again; this is the rule behind
/// the button.
/// </summary>
public sealed class MovieCollectionExclusionTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-03T00:00:00Z");

    private static LibraryItem Library()
        => new(
            Id: "library-movies", Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "/media/movies",
            DownloadsPath: null, QualityProfileId: null, QualityProfileName: null, CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true, UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null,
            ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true,
            MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12,
            MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active",
            SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: Stamp, UpdatedUtc: Stamp);

    private static MovieCollectionItem Collection()
        => new(
            "collection-1", "tmdb", "645", "Nolan Collection", null, null, null, "library-movies", "Movies", "/media/movies",
            Monitored: true, MonitorMovies: true, QualityProfileId: null, QualityProfileName: null, MinimumAvailability: "released",
            SearchOnAdd: true, MemberCount: 2, HeldCount: 0, MissingCount: 2, LastSyncedUtc: null, NextSyncUtc: null, LastSyncError: null,
            CreatedUtc: Stamp, UpdatedUtc: Stamp, Tags: "");

    private static MovieCollectionMemberItem Member(string providerId, string title, bool excluded)
        => new("collection-1", providerId, title, 2010, null, null, null, null, null, null, excluded, Stamp);

    private static MovieListItem Movie(string id, string title)
        => new(id, title, 2010, null, true, false, "tmdb", "1", null, null, null, null, null, [], null, null, null, null, Stamp, Stamp);

    private static (MovieCollectionService Service, Mock<IMovieCatalogRepository> Movies, Mock<IJobQueueRepository> Jobs) Build(params MovieCollectionMemberItem[] members)
    {
        var collections = new Mock<IMovieCollectionsRepository>();
        collections.Setup(repo => repo.GetAsync("collection-1", It.IsAny<CancellationToken>())).ReturnsAsync(Collection());
        collections.Setup(repo => repo.ListMembersAsync("collection-1", It.IsAny<CancellationToken>())).ReturnsAsync(members);
        collections.Setup(repo => repo.LinkMovieAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(provider => provider.GetMovieCollectionAsync("645", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataCollection("tmdb", "645", "Nolan Collection", null, null, null, []));

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.FindExistingIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateMovieRequest request, CancellationToken _) => Movie("movie-" + request.Title, request.Title ?? ""));
        movies.Setup(repo => repo.EnsureWantedStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var decisions = new Mock<IMediaDecisionService>();
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(queue => queue.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new MovieCollectionService(
            collections.Object, metadata.Object, movies.Object, libraries.Object, new Mock<IQualityRepository>().Object,
            decisions.Object, jobs.Object, new FixedClock(Stamp), null, null);
        return (service, movies, jobs);
    }

    [Fact]
    public async Task An_excluded_film_is_not_added_not_made_wanted_and_not_counted_as_added()
    {
        var (service, movies, _) = Build(Member("1", "Wanted film", excluded: false), Member("2", "Excluded film", excluded: true));

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.ExcludedCount);
        movies.Verify(repo => repo.AddAsync(It.Is<CreateMovieRequest>(request => request.Title == "Excluded film"), It.IsAny<CancellationToken>()), Times.Never);
        movies.Verify(repo => repo.AddAsync(It.Is<CreateMovieRequest>(request => request.Title == "Wanted film"), It.IsAny<CancellationToken>()), Times.Once);
        movies.Verify(repo => repo.EnsureWantedStateAsync("movie-Excluded film", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_collection_whose_only_missing_films_are_excluded_adds_nothing_and_searches_for_nothing()
    {
        var (service, movies, jobs) = Build(Member("1", "Excluded film", excluded: true));

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(0, result.AddedCount);
        Assert.False(result.SearchRequested);
        movies.Verify(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        jobs.Verify(queue => queue.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
