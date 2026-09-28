using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Deluno.Worker.Seerr;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Seerr;

public sealed class SeerrAcquisitionServiceTests
{
    private static SeerrConnection Connection(string? movieLibraryId = null, string? movieQualityProfileId = null)
        => new(
            Id: "seerr-1",
            Name: "Lab Seerr",
            BaseUrl: "http://127.0.0.1:5055",
            ApiKey: "key-1",
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ServerName: "Lab Seerr",
            Version: "3.4.1",
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            MovieLibraryId: movieLibraryId,
            MovieQualityProfileId: movieQualityProfileId);

    private static LibraryItem MovieLibrary(string id = "library-movies")
        => new(
            Id: id,
            Name: id,
            MediaType: "movies",
            Purpose: "Movie collection",
            RootPath: "D:\\Media\\Movies",
            DownloadsPath: null,
            QualityProfileId: null,
            QualityProfileName: null,
            CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            ImportWorkflow: "direct",
            ProcessorName: null,
            ProcessorOutputPath: null,
            ProcessorTimeoutMinutes: 60,
            ProcessorFailureMode: "hold",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalMinutes: 6,
            RetryDelayHours: 12,
            MaxItemsPerRun: 50,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            AutomationStatus: "active",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"));

    private static MetadataSearchResult Movie()
        => new("tmdb", "603", "movies", "The Matrix", null, 1999, null, null, null, null, [], [], "tt0133093", null);

    [Fact]
    public async Task AcquireAsync_adds_the_movie_to_the_connections_mapped_library_and_requests_a_search()
    {
        var connection = Connection(movieLibraryId: "library-movies", movieQualityProfileId: "profile-1");
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([MovieLibrary("library-movies")]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.FindExistingIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieListItem(
                "movie-matrix", "The Matrix", 1999, "tt0133093", true, false, null, null, null, null, null, null, null,
                [], null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        movies.Setup(repo => repo.EnsureWantedStateAsync(
                "movie-matrix", "library-movies", It.IsAny<string>(), It.IsAny<string>(), false,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(provider => provider.ResolveProviderRecordAsync(
                It.Is<MetadataLookupRequest>(request => request.ProviderId == "603" && request.IdSource == "tmdb"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "603", Movie()));

        var decisions = new Mock<IMediaDecisionService>();
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue.Setup(repo => repo.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var activity = new Mock<IActivityFeedRepository>();
        var acquisitions = new Mock<ISeerrAcquisitionsRepository>();

        var service = new SeerrAcquisitionService(
            libraries.Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            metadata.Object,
            decisions.Object,
            jobQueue.Object,
            activity.Object,
            acquisitions.Object,
            NullLogger<SeerrAcquisitionService>.Instance);

        var outcome = await service.AcquireAsync(connection, "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 42, CancellationToken.None);

        Assert.True(outcome.Ok);
        movies.Verify(repo => repo.AddAsync(It.Is<CreateMovieRequest>(request => request.Title == "The Matrix"), It.IsAny<CancellationToken>()), Times.Once);
        movies.Verify(repo => repo.UpdateQualityProfileAsync("movie-matrix", "profile-1", It.IsAny<CancellationToken>()), Times.Once);
        jobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-movies"), It.IsAny<CancellationToken>()), Times.Once);
        acquisitions.Verify(repo => repo.RecordAsync(connection.Id, connection.Name, "movies", "movie-matrix", "The Matrix", 42, "added", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_records_a_failure_when_deluno_has_no_matching_library()
    {
        var connection = Connection();
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var activity = new Mock<IActivityFeedRepository>();
        var acquisitions = new Mock<ISeerrAcquisitionsRepository>();

        var service = new SeerrAcquisitionService(
            libraries.Object,
            new Mock<IMovieCatalogRepository>().Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IJobQueueRepository>().Object,
            activity.Object,
            acquisitions.Object,
            NullLogger<SeerrAcquisitionService>.Instance);

        var outcome = await service.AcquireAsync(connection, "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 42, CancellationToken.None);

        Assert.False(outcome.Ok);
        acquisitions.Verify(repo => repo.RecordAsync(connection.Id, connection.Name, "movies", string.Empty, It.IsAny<string>(), 42, "failed", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
