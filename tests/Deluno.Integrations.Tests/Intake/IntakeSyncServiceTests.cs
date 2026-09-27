using System.Net;
using System.Text;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Trakt;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Intake;

public sealed class IntakeSyncServiceTests
{
    [Fact]
    public async Task RunAsync_fetches_a_plain_list_adds_the_title_and_records_its_origin()
    {
        var source = new IntakeSourceItem(
            Id: "source-watchlist",
            Name: "Weekend watchlist",
            Provider: "url-list",
            FeedUrl: "https://lists.deluno.test/weekend.txt",
            MediaType: "movies",
            LibraryId: "library-movies",
            LibraryName: "Movies",
            QualityProfileId: null,
            QualityProfileName: null,
            RequiredGenres: string.Empty,
            MinimumRating: null,
            MinimumYear: null,
            MaximumAgeDays: null,
            AllowedCertifications: string.Empty,
            Audience: "any",
            SyncIntervalHours: 12,
            LastSyncUtc: null,
            LastSyncStatus: "never",
            LastSyncSummary: null,
            SearchOnAdd: false,
            IsEnabled: true,
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var library = new LibraryItem(
            Id: "library-movies",
            Name: "Movies",
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
            SearchIntervalHours: 6,
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
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();
        CreateIntakeTitleOriginRequest? recordedOrigin = null;
        CreateMovieRequest? addedMovie = null;

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([library]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeTitleOriginAsync(It.IsAny<CreateIntakeTitleOriginRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateIntakeTitleOriginRequest, CancellationToken>((request, _) => recordedOrigin = request)
            .ReturnsAsync((IntakeTitleOriginItem?)null);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(
                source.Id,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);

        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieRequest, CancellationToken>((request, _) => addedMovie = request)
            .ReturnsAsync(new MovieListItem(
                "movie-arrival",
                "Arrival",
                2016,
                null,
                true,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-14T00:00:00Z")));
        movies.Setup(repo => repo.EnsureWantedStateAsync(
                "movie-arrival",
                library.Id,
                It.IsAny<string>(),
                It.IsAny<string>(),
                false,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            metadata.Object,
            decisions.Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var result = await service.RunAsync(source.Id, relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, result.FetchedCount);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.NotNull(addedMovie);
        Assert.Equal("Arrival", addedMovie!.Title);
        Assert.Equal(2016, addedMovie.ReleaseYear);
        Assert.NotNull(recordedOrigin);
        Assert.Equal(source.Id, recordedOrigin!.SourceId);
        Assert.Equal(source.Name, recordedOrigin.SourceName);
        Assert.Equal("url-list", recordedOrigin.Provider);
        Assert.Equal("movies", recordedOrigin.MediaType);
        Assert.Equal("movie-arrival", recordedOrigin.EntityId);
        Assert.Equal("title:arrival:2016", recordedOrigin.EntryKey);

        // The dedupe check is a lookup per list entry, not the whole catalogue.
        // Loading every movie to answer "do I already have this?" is the one
        // shape that gets slower as the library grows.
        movies.Verify(repo => repo.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
        movies.Verify(
            repo => repo.FindExistingIdAsync(
                "Arrival",
                2016,
                It.Is<string?>(value => string.IsNullOrEmpty(value)),
                null,
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_counts_a_title_already_in_the_catalogue_as_a_duplicate_without_adding_it()
    {
        var source = CreateWatchlistSource();
        var library = CreateMovieLibrary();
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([library]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeTitleOriginAsync(It.IsAny<CreateIntakeTitleOriginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeTitleOriginItem?)null);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(
                source.Id,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        movies.Setup(repo => repo.FindExistingIdAsync(
                "Arrival",
                2016,
                It.Is<string?>(value => string.IsNullOrEmpty(value)),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("movie-arrival");

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            metadata.Object,
            decisions.Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var result = await service.RunAsync(source.Id, relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, result.FetchedCount);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.DuplicateCount);
        movies.Verify(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        movies.Verify(repo => repo.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>#848: a movie source with monitor mode "None" adds the title unmonitored instead of Deluno's old hardcoded fully-monitored add.</summary>
    [Fact]
    public async Task RunAsync_adds_a_movie_unmonitored_when_the_list_chooses_the_none_monitor_mode()
    {
        var source = CreateWatchlistSource() with { MonitorMode = "none" };
        var library = CreateMovieLibrary();
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();
        CreateMovieRequest? addedMovie = null;

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeTitleOriginAsync(It.IsAny<CreateIntakeTitleOriginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeTitleOriginItem?)null);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(
                source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        movies.Setup(repo => repo.FindExistingIdAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieRequest, CancellationToken>((request, _) => addedMovie = request)
            .ReturnsAsync(new MovieListItem(
                "movie-arrival", "Arrival", 2016, null, false, false, null, null, null, null, null, null, null,
                [], null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        movies.Setup(repo => repo.EnsureWantedStateAsync(
                "movie-arrival", library.Id, It.IsAny<string>(), It.IsAny<string>(), false,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            metadata.Object,
            decisions.Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var result = await service.RunAsync(source.Id, relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, result.AddedCount);
        Assert.NotNull(addedMovie);
        Assert.False(addedMovie!.Monitored);
    }

    /// <summary>#848/#850: a TV source carries its chosen monitor mode, series type and season folder override onto the series it adds.</summary>
    [Fact]
    public async Task RunAsync_adds_a_series_with_the_lists_monitor_mode_series_type_and_season_folder()
    {
        var source = CreateWatchlistSource() with
        {
            MediaType = "tv",
            LibraryId = "library-tv",
            MonitorMode = "future",
            SeriesType = "anime",
            SeasonFolder = true
        };
        var library = CreateMovieLibrary() with { Id = "library-tv", MediaType = "tv" };
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();
        CreateSeriesRequest? addedSeries = null;

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeTitleOriginAsync(It.IsAny<CreateIntakeTitleOriginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeTitleOriginItem?)null);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(
                source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        series.Setup(repo => repo.FindExistingIdAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        series.Setup(repo => repo.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateSeriesRequest, CancellationToken>((request, _) => addedSeries = request)
            .ReturnsAsync(new SeriesListItem(
                Id: "series-arrival",
                Title: "Arrival",
                StartYear: 2016,
                ImdbId: null,
                Monitored: true,
                HasFile: false,
                MetadataProvider: null,
                MetadataProviderId: null,
                OriginalTitle: null,
                Overview: null,
                PosterUrl: null,
                BackdropUrl: null,
                Rating: null,
                Ratings: [],
                Genres: null,
                ExternalUrl: null,
                MetadataJson: null,
                MetadataUpdatedUtc: null,
                CreatedUtc: DateTimeOffset.UnixEpoch,
                UpdatedUtc: DateTimeOffset.UnixEpoch));
        series.Setup(repo => repo.EnsureWantedStateAsync(
                "series-arrival", library.Id, It.IsAny<string>(), It.IsAny<string>(), false,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object,
            series.Object,
            metadata.Object,
            decisions.Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var result = await service.RunAsync(source.Id, relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, result.AddedCount);
        Assert.NotNull(addedSeries);
        Assert.Equal("future", addedSeries!.ListMonitorMode);
        Assert.Equal("anime", addedSeries.SeriesType);
        Assert.True(addedSeries.SeasonFolder);
    }

    private static IntakeSourceItem CreateWatchlistSource()
        => new(
            Id: "source-watchlist",
            Name: "Weekend watchlist",
            Provider: "url-list",
            FeedUrl: "https://lists.deluno.test/weekend.txt",
            MediaType: "movies",
            LibraryId: "library-movies",
            LibraryName: "Movies",
            QualityProfileId: null,
            QualityProfileName: null,
            RequiredGenres: string.Empty,
            MinimumRating: null,
            MinimumYear: null,
            MaximumAgeDays: null,
            AllowedCertifications: string.Empty,
            Audience: "any",
            SyncIntervalHours: 12,
            LastSyncUtc: null,
            LastSyncStatus: "never",
            LastSyncSummary: null,
            SearchOnAdd: false,
            IsEnabled: true,
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"));

    private static LibraryItem CreateMovieLibrary()
        => new(
            Id: "library-movies",
            Name: "Movies",
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
            SearchIntervalHours: 6,
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

    [Fact]
    public async Task PreviewAsync_fetches_a_public_mdblist_url_as_a_custom_list_without_an_api_key()
    {
        var source = new IntakeSourceItem(
            Id: "source-mdblist",
            Name: "90 Day Fiancé",
            Provider: "url-list",
            FeedUrl: "https://mdblist.com/lists/ibtrashcan000/90-day-fiance",
            MediaType: "tv",
            LibraryId: null,
            LibraryName: null,
            QualityProfileId: null,
            QualityProfileName: null,
            RequiredGenres: string.Empty,
            MinimumRating: null,
            MinimumYear: null,
            MaximumAgeDays: null,
            AllowedCertifications: string.Empty,
            Audience: "any",
            SyncIntervalHours: 12,
            LastSyncUtc: null,
            LastSyncStatus: "never",
            LastSyncSummary: null,
            SearchOnAdd: false,
            IsEnabled: true,
            CreatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var handler = new PublicMdbListHandler();
        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object,
            series.Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(handler),
            NullLogger<IntakeSyncService>.Instance);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(1, preview.FetchedCount);
        Assert.Single(preview.Items);
        Assert.Equal("90 Day Fiancé", preview.Items[0].Title);
        Assert.Equal("tt3469050", preview.Items[0].ImdbId);
        Assert.Equal("Sonarr/4.0", handler.UserAgent);
        Assert.Equal("application/json", handler.Accept);
    }

    [Fact]
    public async Task PreviewAsync_fetches_selected_tmdb_person_roles_and_keeps_the_source_media_type()
    {
        var source = CreateWatchlistSource() with
        {
            Provider = "tmdb-person",
            FeedUrl = "https://www.themoviedb.org/person/6384?credits=cast,director",
            MediaType = "movies"
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        platform.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tmdb-secret");
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        movies.Setup(repo => repo.FindExistingIdAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new TmdbPersonHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(2, preview.FetchedCount);
        Assert.Collection(
            preview.Items,
            first =>
            {
                Assert.Equal("The Matrix", first.Title);
                Assert.Equal("movies", first.MediaType);
            },
            second =>
            {
                Assert.Equal("Inception", second.Title);
                Assert.Equal("movies", second.MediaType);
            });
        movies.Verify(repo => repo.FindExistingIdAsync(
            "The Matrix",
            1999,
            null,
            "tmdb",
            "603",
            It.IsAny<CancellationToken>()), Times.Once);
        movies.Verify(repo => repo.FindExistingIdAsync(
            "Inception",
            2010,
            null,
            "tmdb",
            "27205",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_withholds_the_search_for_a_movie_that_has_not_reached_the_lists_minimum_availability()
    {
        var source = CreateWatchlistSource() with
        {
            Id = "source-tmdb-list",
            Provider = "tmdb",
            FeedUrl = "12345",
            SearchOnAdd = true,
            MinimumAvailability = "released"
        };
        var library = CreateMovieLibrary();
        var platform = new Mock<IPlatformSettingsRepository>();
        platform.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tmdb-secret");
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeTitleOriginAsync(It.IsAny<CreateIntakeTitleOriginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeTitleOriginItem?)null);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(
                source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        movies.Setup(repo => repo.FindExistingIdAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieListItem(
                "movie-notyet", "Not Yet Released", 2099, null, true, false, null, null, null, null, null, null, null,
                [], null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        movies.Setup(repo => repo.EnsureWantedStateAsync(
                "movie-notyet", library.Id, It.IsAny<string>(), It.IsAny<string>(), false,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Not out yet, so the wanted-state decision itself reflects IsReleased
        // = false the same way movie collections and manual adds do.
        decisions.Setup(service => service.DecideWantedState(It.Is<MediaWantedDecisionInput>(input => !input.IsReleased)))
            .Returns(new LibraryQualityDecision("upcoming", "This movie is not out yet.", false, null, "WEBDL-1080p", "test"));

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            metadata.Object,
            decisions.Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new TmdbFutureReleaseListHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var result = await service.RunAsync(source.Id, relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, result.AddedCount);
        // The gate: search-on-add is true and the title was added, but its
        // release date has not reached "released" yet, so nothing is queued.
        Assert.False(result.SearchRequested);
        movies.Verify(repo => repo.UpdateMinimumAvailabilityAsync("movie-notyet", "released", It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class TmdbFutureReleaseListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("api.themoviedb.org/3/list/12345", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"items\":[{\"id\":1,\"title\":\"Not Yet Released\",\"release_date\":\"2099-01-01\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    [Theory]
    [InlineData("tmdb-popular", "", "/movie/popular")]
    [InlineData("tmdb-discover", "9715", "/discover/movie")]
    [InlineData("tmdb-company-credits", "1", "/discover/movie")]
    public async Task PreviewAsync_fetches_each_new_opt_in_tmdb_source_type(string provider, string feedUrl, string expectedPathFragment)
    {
        var source = CreateWatchlistSource() with
        {
            Id = $"source-{provider}",
            Provider = provider,
            FeedUrl = feedUrl
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        platform.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tmdb-secret");
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([CreateMovieLibrary()]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var handler = new TmdbResultsHandler(expectedPathFragment);
        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(handler),
            NullLogger<IntakeSyncService>.Instance);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(1, preview.FetchedCount);
        Assert.Equal("Dune", preview.Items[0].Title);
        Assert.Equal(2021, preview.Items[0].Year);
    }

    [Fact]
    public async Task PreviewAsync_fetches_every_movie_in_a_tmdb_collection()
    {
        var source = CreateWatchlistSource() with
        {
            Id = "source-tmdb-collection",
            Provider = "tmdb-collection",
            FeedUrl = "263"
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        platform.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tmdb-secret");
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([CreateMovieLibrary()]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new TmdbCollectionHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(2, preview.FetchedCount);
        Assert.Equal("Dune", preview.Items[0].Title);
        Assert.Equal("Dune: Part Two", preview.Items[1].Title);
    }

    private sealed class TmdbResultsHandler(string expectedPathFragment) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains(expectedPathFragment, request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"id\":438631,\"title\":\"Dune\",\"release_date\":\"2021-10-22\",\"vote_average\":8.0}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class TmdbCollectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("api.themoviedb.org/3/collection/263", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"parts\":[{\"id\":438631,\"title\":\"Dune\",\"release_date\":\"2021-10-22\"},{\"id\":693134,\"title\":\"Dune: Part Two\",\"release_date\":\"2024-02-27\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    [Fact]
    public async Task PreviewAsync_reads_traktS_built_in_trending_list_once_connected()
    {
        var source = CreateWatchlistSource() with
        {
            Id = "source-trakt-trending",
            Provider = "trakt-trending",
            FeedUrl = string.Empty
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([CreateMovieLibrary()]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var tokenProvider = new Mock<ITraktTokenProvider>();
        tokenProvider.Setup(p => p.GetAuthContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktAuthContext("client-id", "access-1"));
        var traktClient = new Mock<ITraktClient>();
        traktClient.Setup(c => c.GetAsync("client-id", "access-1", "/movies/trending", It.IsAny<CancellationToken>()))
            .ReturnsAsync("[{\"watchers\":10,\"movie\":{\"title\":\"Arrival\",\"year\":2016,\"ids\":{\"imdb\":\"tt2543164\"}}}]");

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance,
            tokenProvider.Object,
            traktClient.Object);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(1, preview.FetchedCount);
        Assert.Equal("Arrival", preview.Items[0].Title);
        Assert.Equal(2016, preview.Items[0].Year);
        Assert.Equal("tt2543164", preview.Items[0].ImdbId);
    }

    [Fact]
    public async Task PreviewAsync_refuses_a_built_in_trakt_list_when_trakt_is_not_connected()
    {
        var source = CreateWatchlistSource() with
        {
            Id = "source-trakt-popular",
            Provider = "trakt-popular",
            FeedUrl = string.Empty
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var tokenProvider = new Mock<ITraktTokenProvider>();
        tokenProvider.Setup(p => p.GetAuthContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync((TraktAuthContext?)null);

        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StubHttpMessageHandler()),
            NullLogger<IntakeSyncService>.Instance,
            tokenProvider.Object,
            Mock.Of<ITraktClient>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PreviewAsync(source.Id, CancellationToken.None));
        Assert.Contains("Connect Trakt", exception.Message);
    }

    [Fact]
    public async Task PreviewAsync_parses_a_stevenlu_feeds_title_and_imdb_id_entries()
    {
        var source = CreateWatchlistSource() with
        {
            Id = "source-stevenlu",
            Provider = "stevenlu",
            FeedUrl = "https://s3.amazonaws.com/popular-movies/movies.json"
        };
        var platform = new Mock<IPlatformSettingsRepository>();
        var libraries = new Mock<ILibrariesRepository>();
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([CreateMovieLibrary()]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = new IntakeSyncService(
            platform.Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            movies.Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new SingleClientFactory(new StevenLuHandler()),
            NullLogger<IntakeSyncService>.Instance);

        var preview = await service.PreviewAsync(source.Id, CancellationToken.None);

        Assert.Equal(2, preview.FetchedCount);
        Assert.Collection(
            preview.Items,
            first =>
            {
                Assert.Equal("Arrival", first.Title);
                Assert.Equal("tt2543164", first.ImdbId);
                Assert.Equal("movies", first.MediaType);
            },
            second =>
            {
                Assert.Equal("Sicario", second.Title);
                Assert.Equal("tt3397884", second.ImdbId);
            });
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://lists.deluno.test/weekend.txt", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Arrival (2016)\n", Encoding.UTF8, "text/plain")
            });
        }
    }

    private sealed class PublicMdbListHandler : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://mdblist.com/lists/ibtrashcan000/90-day-fiance", request.RequestUri?.ToString());
            UserAgent = request.Headers.UserAgent.ToString();
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"title\":\"90 Day Fianc\\u00e9\",\"release_year\":2014,\"imdb_id\":\"tt3469050\",\"mediatype\":\"show\"}]",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class StevenLuHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://s3.amazonaws.com/popular-movies/movies.json", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"title\":\"Arrival\",\"imdb_id\":\"tt2543164\"},{\"title\":\"Sicario\",\"imdb_id\":\"tt3397884\"}]",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class TmdbPersonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("api.themoviedb.org/3/person/6384/combined_credits", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"cast\":[{\"id\":603,\"title\":\"The Matrix\",\"media_type\":\"movie\",\"release_date\":\"1999-03-30\",\"vote_average\":8.2},{\"id\":1396,\"name\":\"Breaking Bad\",\"media_type\":\"tv\",\"first_air_date\":\"2008-01-20\"}],\"crew\":[{\"id\":27205,\"title\":\"Inception\",\"media_type\":\"movie\",\"release_date\":\"2010-07-15\",\"department\":\"Directing\",\"job\":\"Director\"},{\"id\":27205,\"title\":\"Inception\",\"media_type\":\"movie\",\"release_date\":\"2010-07-15\",\"department\":\"Production\",\"job\":\"Producer\"}]} ",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
