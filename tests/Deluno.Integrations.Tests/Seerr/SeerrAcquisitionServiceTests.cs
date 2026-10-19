using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
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
        string? seriesQualityProfileId = null,
        string tags = "",
        string tags4k = "")
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
            SeriesQualityProfileId: seriesQualityProfileId,
            Tags: tags,
            Tags4k: tags4k);

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

        public SeerrAcquisitionService BuildService(ITitleTagService? titleTags = null)
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
                NullLogger<SeerrAcquisitionService>.Instance,
                titleTags);

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

    /// <summary>
    /// #1046: the connection's tags are given to the title the moment it exists - before
    /// the search is requested and before the Activity line - and the tags routing sees
    /// include them, so a tag destination rule places the title.
    /// </summary>
    [Fact]
    public async Task A_seerr_added_title_is_tagged_before_anything_else_and_a_tag_rule_places_it()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), MovieLibrary("library-kids")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-kids", "tag", "kids", "library-kids")]);
        var movie = Movie();
        fixture.SetupHappyMoviePath(movie, "library-kids");

        var order = new List<string>();
        var tags = new Mock<ITitleTagService>();
        tags.Setup(service => service.GiveAsync(MediaKind.Movie, "movie-matrix", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("tagged"))
            .ReturnsAsync(["kids", "requested-by-seerr"]);
        fixture.JobQueue.Setup(repo => repo.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("search"))
            .ReturnsAsync(true);
        fixture.Activity.Setup(repo => repo.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("activity"))
            .ReturnsAsync((ActivityEventItem)null!);

        var outcome = await fixture.BuildService(tags.Object).AcquireAsync(
            Connection(movieLibraryId: "library-movies", tags: "kids, requested-by-seerr"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 50, is4k: false, requestedBy: "Ben", CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Contains("Tagged kids, requested-by-seerr", outcome.Message);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-kids"), It.IsAny<CancellationToken>()), Times.Once);
        tags.Verify(service => service.GiveAsync(MediaKind.Movie, "movie-matrix",
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "kids", "requested-by-seerr" })), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["tagged", "search", "activity"], order);
    }

    [Fact]
    public async Task A_4k_request_also_gets_the_connections_extra_4k_tags_and_a_normal_one_does_not()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), MovieLibrary("library-movies-4k")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-4k", "requested4k", "true", "library-movies-4k")]);
        fixture.SetupHappyMoviePath(Movie(), "library-movies-4k");
        var tags = new Mock<ITitleTagService>();
        tags.Setup(service => service.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var connection = Connection(movieLibraryId: "library-movies", tags: "seerr", tags4k: "4k");

        await fixture.BuildService(tags.Object).AcquireAsync(connection, "movies", "603", null, 51, is4k: true, requestedBy: null, CancellationToken.None);
        tags.Verify(service => service.GiveAsync(MediaKind.Movie, "movie-matrix",
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "seerr", "4k" })), It.IsAny<CancellationToken>()), Times.Once);

        tags.Invocations.Clear();
        await fixture.BuildService(tags.Object).AcquireAsync(connection, "movies", "603", null, 52, is4k: false, requestedBy: null, CancellationToken.None);
        tags.Verify(service => service.GiveAsync(MediaKind.Movie, "movie-matrix",
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "seerr" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_connection_with_no_tags_gives_none()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fixture.SetupHappyMoviePath(Movie(), "library-movies");
        var tags = new Mock<ITitleTagService>();

        var outcome = await fixture.BuildService(tags.Object).AcquireAsync(
            Connection(movieLibraryId: "library-movies"), "movies", "603", null, 53, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.DoesNotContain("Tagged", outcome.Message);
        tags.Verify(service => service.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
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

    /// <summary>#1035: a 4K request no rule places is refused even with a fallback library set.</summary>
    [Fact]
    public async Task An_unmatched_4k_request_is_refused_even_when_a_fallback_library_is_set()
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
            Connection(movieLibraryId: "library-movies", movieQualityProfileId: "profile-1"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 48, is4k: true, requestedBy: "James", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("no routing rule placed it", outcome.Message);
        Assert.Contains("requested in 4K", outcome.Message);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.IsAny<LibraryAutomationPlanItem>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Activity.Verify(repo => repo.RecordActivityAsync("seerr.acquire-failed", It.Is<string>(m => m.Contains("requested in 4K")), null, null, "seerr-server", "seerr-1", It.IsAny<CancellationToken>()), Times.Once);
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

    [Fact]
    public async Task The_TMDb_id_is_what_the_title_is_looked_up_by_when_both_ids_are_sent_and_the_TVDb_id_only_when_it_is_the_only_one()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([MovieLibrary("library-movies")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var asked = new List<MetadataLookupRequest>();
        fixture.SetupHappyMoviePath(Movie(), "library-movies");
        fixture.Metadata.Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MetadataLookupRequest, CancellationToken>((request, _) => asked.Add(request))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "603", Movie()));
        var service = fixture.BuildService();

        await service.AcquireAsync(Connection(movieLibraryId: "library-movies"), "movies", tmdbId: "603", tvdbId: "81189", seerrMediaId: 1, is4k: false, requestedBy: null, CancellationToken.None);
        await service.AcquireAsync(Connection(movieLibraryId: "library-movies"), "movies", tmdbId: null, tvdbId: "81189", seerrMediaId: 2, is4k: false, requestedBy: null, CancellationToken.None);
        await service.AcquireAsync(Connection(movieLibraryId: "library-movies"), "movies", tmdbId: " ", tvdbId: "81189", seerrMediaId: 3, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.Equal([("tmdb", "603"), ("tvdb", "81189"), ("tvdb", "81189")], asked.Select(request => (request.IdSource, request.ProviderId)));
    }

    [Fact]
    public async Task An_unexpected_error_becomes_a_failed_acquisition_in_plain_words_and_is_never_thrown()
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([MovieLibrary("library-movies")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fixture.Metadata.Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Sequence contains no elements at Deluno.Internal.Thing.Method()"));

        var outcome = await fixture.BuildService().AcquireAsync(Connection(), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 9, is4k: false, requestedBy: "Ben", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.DoesNotContain("at Deluno", outcome.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
        fixture.Acquisitions.Verify(repo => repo.RecordAsync(
            "seerr-1", "Lab Seerr", "movies", string.Empty, "Unexpected error", 9, "failed", outcome.Message, "Ben", It.IsAny<CancellationToken>(), null, null), Times.Once);
        fixture.Activity.Verify(repo => repo.RecordActivityAsync("seerr.acquire-failed", $"Lab Seerr: {outcome.Message}", null, null, "seerr-server", "seerr-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, AutoTagTrigger.Added)]
    [InlineData(true, AutoTagTrigger.Edited)]
    public async Task The_tags_the_auto_tagging_rules_would_give_are_known_before_routing_so_a_tag_rule_places_the_request_and_the_rules_run_again_once_the_title_exists(bool alreadyInTheCatalogue, AutoTagTrigger trigger)
    {
        var fixture = new Fixture();
        fixture.Libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MovieLibrary("library-movies"), MovieLibrary("library-kids")]);
        fixture.Libraries.Setup(repo => repo.ListDestinationRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("rule-kids", "tag", "kids", "library-kids")]);
        fixture.SetupHappyMoviePath(Movie(), "library-kids");
        if (alreadyInTheCatalogue)
        {
            fixture.Movies.Setup(repo => repo.FindExistingIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("movie-matrix");
        }

        var autoTagging = new Mock<IAutoTaggingService>();
        autoTagging.Setup(service => service.PredictAsync(It.IsAny<AutoTagFacts>(), It.IsAny<CancellationToken>())).ReturnsAsync(["kids"]);
        autoTagging.Setup(service => service.ApplyAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<AutoTagTrigger>(), It.IsAny<AutoTagAddFacts?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(AutoTagOutcome)!);
        var service = new SeerrAcquisitionService(
            fixture.Libraries.Object, new LibraryRoutingResolver(), fixture.Movies.Object, fixture.Series.Object, fixture.Metadata.Object, fixture.Decisions.Object,
            fixture.JobQueue.Object, fixture.Activity.Object, fixture.Acquisitions.Object, NullLogger<SeerrAcquisitionService>.Instance, null, autoTagging.Object);

        var outcome = await service.AcquireAsync(Connection(movieLibraryId: "library-movies"), "movies", tmdbId: "603", tvdbId: null, seerrMediaId: 50, is4k: false, requestedBy: null, CancellationToken.None);

        Assert.True(outcome.Ok);
        fixture.JobQueue.Verify(repo => repo.RequestLibrarySearchAsync(It.Is<LibraryAutomationPlanItem>(item => item.LibraryId == "library-kids"), It.IsAny<CancellationToken>()), Times.Once);
        autoTagging.Verify(service => service.PredictAsync(It.IsAny<AutoTagFacts>(), It.IsAny<CancellationToken>()), Times.Once);
        autoTagging.Verify(service => service.ApplyAsync(MediaKind.Movie, "movie-matrix", trigger, It.IsAny<AutoTagAddFacts?>(), It.Is<IReadOnlyList<string>?>(libraries => libraries != null && libraries.SequenceEqual(new[] { "library-kids" })), It.IsAny<CancellationToken>()), Times.Once);
    }
}
