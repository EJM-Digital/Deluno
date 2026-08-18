using System.Net;
using System.Text;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
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
        var intake = new Mock<IIntakeRepository>();
        var movies = new Mock<IMovieCatalogRepository>();
        var metadata = new Mock<IMetadataProvider>();
        var decisions = new Mock<IMediaDecisionService>();
        CreateIntakeTitleOriginRequest? recordedOrigin = null;
        CreateMovieRequest? addedMovie = null;

        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        platform.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
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

        movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
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
            .Returns(new LibraryQualityDecision("wanted", "Missing from the library.", false, null, "WEBDL-1080p", "test"));

        var service = new IntakeSyncService(
            platform.Object,
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
    }

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
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        platform.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var handler = new PublicMdbListHandler();
        var service = new IntakeSyncService(
            platform.Object,
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
}
