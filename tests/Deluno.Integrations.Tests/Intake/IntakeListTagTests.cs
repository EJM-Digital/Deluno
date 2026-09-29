using System.Net;
using System.Text;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Intake;

/// <summary>#1015: a list gives its tags to every title it adds (Radarr and Sonarr set Tags = importList.Tags on each added title).</summary>
public sealed class IntakeListTagTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static IntakeSourceItem Source(string tags)
        => new(
            Id: "source-watchlist", Name: "Weekend watchlist", Provider: "url-list", FeedUrl: "https://lists.deluno.test/weekend.txt",
            MediaType: "movies", LibraryId: "library-movies", LibraryName: "Movies", QualityProfileId: null, QualityProfileName: null,
            RequiredGenres: string.Empty, MinimumRating: null, MinimumYear: null, MaximumAgeDays: null, AllowedCertifications: string.Empty,
            Audience: "any", SyncIntervalHours: 12, LastSyncUtc: null, LastSyncStatus: "never", LastSyncSummary: null, SearchOnAdd: false,
            IsEnabled: true, CreatedUtc: Stamp, UpdatedUtc: Stamp, Tags: tags);

    private static LibraryItem Library()
        => new(
            Id: "library-movies", Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "D:\\Media\\Movies",
            DownloadsPath: null, QualityProfileId: null, QualityProfileName: null, CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true, UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null,
            ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true,
            MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12,
            MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active",
            SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: Stamp, UpdatedUtc: Stamp);

    private static MovieListItem Movie(string id, string title)
        => new(id, title, 2016, null, true, false, null, null, null, null, null, null, null, [], null, null, null, null, Stamp, Stamp);

    private static (IntakeSyncService Service, Mock<ITitleTagService> Tags, List<string> Order, Mock<IActivityFeedRepository> Activity) Build(string tags)
    {
        var source = Source(tags);
        var order = new List<string>();
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.FindExistingIdAsync("Arrival", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        movies.Setup(repo => repo.FindExistingIdAsync("Already Here", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("movie-old");
        movies.Setup(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Movie("movie-arrival", "Arrival"));
        movies.Setup(repo => repo.EnsureWantedStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wanted")).Returns(Task.CompletedTask);
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var decisions = new Mock<IMediaDecisionService>();
        decisions.Setup(service => service.DecideWantedState(It.IsAny<MediaWantedDecisionInput>()))
            .Returns(new LibraryQualityDecision("missing", "Missing from the library.", false, null, "WEBDL-1080p", "test"));
        var titleTags = new Mock<ITitleTagService>();
        titleTags.Setup(service => service.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("tagged")).ReturnsAsync((MediaKind _, string _, IEnumerable<string> names, CancellationToken _) => names.ToArray());
        var activity = new Mock<IActivityFeedRepository>();

        var service = new IntakeSyncService(
            new Mock<IPlatformSettingsRepository>().Object, libraries.Object, intake.Object, new Mock<Deluno.Jobs.Data.IJobScheduler>().Object, new Mock<IJobQueueRepository>().Object,
            movies.Object, new Mock<ISeriesCatalogRepository>().Object, metadata.Object, decisions.Object, activity.Object,
            new ConfigurationBuilder().Build(), TimeProvider.System, new ListFactory(), NullLogger<IntakeSyncService>.Instance, titleTagService: titleTags.Object);
        return (service, titleTags, order, activity);
    }

    [Fact]
    public async Task A_title_the_list_adds_gets_the_lists_tags_before_its_wanted_state_and_a_title_already_held_does_not()
    {
        var (service, tags, order, activity) = Build("from-watchlist, kids");

        var result = await service.RunAsync("source-watchlist", relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        tags.Verify(t => t.GiveAsync(MediaKind.Movie, "movie-arrival",
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "from-watchlist", "kids" })), It.IsAny<CancellationToken>()), Times.Once);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), "movie-old", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("tagged", order[0]);
        activity.Verify(a => a.RecordActivityAsync("intake.tagged", It.Is<string>(m => m.Contains("Weekend watchlist") && m.Contains("from-watchlist, kids") && m.Contains("Arrival")),
            It.IsAny<string?>(), It.IsAny<string?>(), "intake-source", "source-watchlist", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_list_with_no_tags_gives_none()
    {
        var (service, tags, _, activity) = Build("");

        var result = await service.RunAsync("source-watchlist", relatedJobId: null, manual: true, CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(a => a.RecordActivityAsync("intake.tagged", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class ListFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Arrival (2016)\nAlready Here (2015)\n", Encoding.UTF8, "text/plain") });
        }
    }
}
