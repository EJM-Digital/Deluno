using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Libraries.Routing;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Deluno.Worker.Seerr;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>
/// #1033: a Seerr acquisition is placed by the same library routing rules a
/// manual add uses (<see cref="ILibraryRoutingResolver"/>), evaluated on the
/// title's metadata, with the connection's own library as the last-resort
/// fallback and a plain-words refusal when neither a rule nor a fallback
/// resolves. Uses the real resolver rather than a mock, since its matching
/// logic is exactly what these scenarios exist to exercise.
/// </summary>
public sealed class SeerrAcquisitionServiceTests
{
    private static SeerrConnection Connection(
        string? movieLibraryId = null,
        string? movieQualityProfileId = null,
        string? seriesLibraryId = null,
        string? seriesQualityProfileId = null)
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
            MovieQualityProfileId: movieQualityProfileId,
            SeriesLibraryId: seriesLibraryId,
            SeriesQualityProfileId: seriesQualityProfileId);

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

    private static DestinationRuleItem Rule(string id, string matchKind, string matchValue, string libraryId, string mediaType = "movies", int priority = 10)
        => new(
            Id: id,
            Name: $"{matchKind}:{matchValue}",
            MediaType: mediaType,
            MatchKind: matchKind,
            MatchValue: matchValue,
            RootPath: string.Empty,
            FolderTemplate: null,
            Priority: priority,
            IsEnabled: true,
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            LibraryId: libraryId);

    private static MetadataSearchResult Movie(
        string title = "The Matrix",
        IReadOnlyList<string>? genres = null,
        string? originalLanguage = null)
        => new("tmdb", "603", "movies", title, null, 1999, null, null, null, null, [], genres ?? [], "tt0133093", null, OriginalLanguage: originalLanguage);

    private sealed class Fixture
    {
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();
        public Mock<IMetadataProvider> Metadata { get; } = new();
        public Mock<IMediaDecisionService> Decisions { get; } = new();
        public Mock<IJobQueueRepository> JobQueue { get; } = new();
        public Mock<IActivityFeedRepository> Activity { get; } = new();
        public Mock<ISeerrAcquisitionsRepository> Acquisitions { get; } = new();

        public SeerrAcquisitionService BuildService()
            => new(
                Libraries.Object,
                new LibraryRoutingResolver(),
                Movies.Object,
                Series.Object,
                Metadata.Object,
                Decisions.Object,
                JobQueue.Object,
                Activity.Object,
                Acquisitions.Object,
                NullLogger<SeerrAcquisitionService>.Instance);

        public void SetupHappyMoviePath(MetadataSearchResult movie, string libraryId)
        {
            Metadata.Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "603", movie));
            Movies.Setup(repo => repo.FindExistingIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            Movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MovieListItem(
                    "movie-matrix", movie.Title, movie.Year, movie.ImdbId, true, false, null, null, null, null, null, null, null,
                    [], null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
            Decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
                .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));
            JobQueue.Setup(repo => repo.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }
    }

    [Fact]
    public async Task A_genre_rule_routes_the_request_to_the_library_it_names()
    {
        var fixture = new Fixture();
        var docsLibrary = MovieLibrary("library-docs");
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), docsLibrary]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-docs", "genre", "Documentary", "library-docs")]);

        var movie = Movie(genres: ["Documentary"]);
        fixture.SetupHappyMoviePath(movie, "library-docs");

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(
            Connection(movieLibraryId: "library-movies"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 42, is4k: false, requestedBy: "James", CancellationToken.None);

        Assert.True(outcome.Ok);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-docs"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", "movie-matrix", movie.Title, 42, "added", null, "James", It.IsAny<CancellationToken>(), "rule-docs", "genre:Documentary"), Times.Once);
    }

    /// <summary>Genre 16 (Animation) plus a Japanese original language is the anime heuristic (no dedicated title field exists yet).</summary>
    [Fact]
    public async Task An_anime_rule_routes_animation_with_a_japanese_original_language()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), MovieLibrary("library-anime")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-anime", "anime", "true", "library-anime")]);

        var movie = Movie(title: "Spirited Away", genres: ["Animation"], originalLanguage: "ja");
        fixture.SetupHappyMoviePath(movie, "library-anime");

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(
            Connection(movieLibraryId: "library-movies"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 43, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.True(outcome.Ok);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-anime"), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>#985's per-connection 4K mapping is now a "requested in 4K" rule condition.</summary>
    [Fact]
    public async Task A_requested_4k_rule_routes_a_4k_request_to_the_library_it_names()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), MovieLibrary("library-movies-4k")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-4k", "requested4k", "true", "library-movies-4k")]);

        var movie = Movie();
        fixture.SetupHappyMoviePath(movie, "library-movies-4k");

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(
            Connection(movieLibraryId: "library-movies", movieQualityProfileId: "profile-1"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 44, is4k: true, requestedBy: "Ben", CancellationToken.None);

        Assert.True(outcome.Ok);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-movies-4k"), It.IsAny<CancellationToken>()), Times.Once);
        // A rule-routed request uses the library's own default profile, not the connection's fallback profile.
        fixture.Movies.Verify(repo => repo.UpdateQualityProfileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", "movie-matrix", movie.Title, 44, "added", null, "Ben", It.IsAny<CancellationToken>(), "rule-4k", "requested4k:true"), Times.Once);
    }

    [Fact]
    public async Task No_rule_matches_so_the_connections_fallback_library_is_used()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-anime", "anime", "true", "library-anime")]); // present, but does not match this title

        var movie = Movie(genres: ["Action"]);
        fixture.SetupHappyMoviePath(movie, "library-movies");

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(
            Connection(movieLibraryId: "library-movies", movieQualityProfileId: "profile-1"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 45, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.True(outcome.Ok);
        fixture.Movies.Verify(repo => repo.UpdateQualityProfileAsync("movie-matrix", "profile-1", It.IsAny<CancellationToken>()), Times.Once);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-movies"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", "movie-matrix", movie.Title, 45, "added", null, null, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    /// <summary>No rule matches and no fallback library is set: Deluno refuses in plain words rather than guessing.</summary>
    [Fact]
    public async Task No_rule_matches_and_no_fallback_is_set_so_the_request_is_refused()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        fixture.Metadata.Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "603", Movie()));

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(
            Connection(), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 46, is4k: false, requestedBy: "James", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("No destination rule matched", outcome.Message);
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", string.Empty, It.IsAny<string>(), 46, "failed", It.IsAny<string>(), "James", It.IsAny<CancellationToken>(), null, null), Times.Once);
        fixture.Activity.Verify(repo => repo.RecordActivityAsync("seerr.acquire-failed", It.IsAny<string>(), null, null, "seerr-server", "seerr-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_records_a_failure_when_deluno_has_no_matching_library()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fixture.Metadata.Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "603", Movie()));

        var service = fixture.BuildService();
        var outcome = await service.AcquireAsync(Connection(), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 47, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.False(outcome.Ok);
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", string.Empty, It.IsAny<string>(), 47, "failed", It.IsAny<string>(), null, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }
}
