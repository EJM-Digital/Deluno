using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Intake;

/// <summary>#1046: the optional tag for titles that are no longer on any list (Sonarr's ListSyncTag).</summary>
public sealed class IntakeOffListTagTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static IntakeSourceItem Source()
        => new(
            Id: "source-watchlist", Name: "Weekend watchlist", Provider: "url-list", FeedUrl: "https://lists.deluno.test/weekend.txt",
            MediaType: "movies", LibraryId: "library-movies", LibraryName: "Movies", QualityProfileId: null, QualityProfileName: null,
            RequiredGenres: string.Empty, MinimumRating: null, MinimumYear: null, MaximumAgeDays: null, AllowedCertifications: string.Empty,
            Audience: "any", SyncIntervalHours: 12, LastSyncUtc: null, LastSyncStatus: "never", LastSyncSummary: null, SearchOnAdd: false,
            IsEnabled: true, CreatedUtc: Stamp, UpdatedUtc: Stamp);

    private static LibraryItem Library()
        => new(
            Id: "library-movies", Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "D:\\Media\\Movies",
            DownloadsPath: null, QualityProfileId: null, QualityProfileName: null, CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true, UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null,
            ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true,
            MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12,
            MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active",
            SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: Stamp, UpdatedUtc: Stamp);

    private static PlatformSettingsSnapshot Settings(bool on, string tag)
        => (PlatformSettingsSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)) with
        {
            TagTitlesOffEveryList = on,
            OffListTag = tag
        };

    private static (IntakeSyncService Service, Mock<IIntakeRepository> Intake, Mock<ITitleTagService> Tags, Mock<IActivityFeedRepository> Activity) Build(
        PlatformSettingsSnapshot settings,
        IReadOnlyList<IntakeDroppedTitle> dropped,
        HttpStatusCode listStatus = HttpStatusCode.OK)
    {
        var source = Source();
        var platform = new Mock<IPlatformSettingsRepository>();
        platform.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        intake.Setup(repo => repo.ListActiveIntakeListExclusionsAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        intake.Setup(repo => repo.RecordIntakeSourceSyncResultAsync(source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IntakeSourceItem?)null);
        intake.Setup(repo => repo.MarkDroppedTitlesAsync(source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(dropped);
        var tags = new Mock<ITitleTagService>();
        tags.Setup(service => service.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaKind _, string _, IEnumerable<string> names, CancellationToken _) => names.ToArray());
        var activity = new Mock<IActivityFeedRepository>();

        var service = new IntakeSyncService(
            platform.Object, libraries.Object, intake.Object, new Mock<IJobScheduler>().Object, new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object, new Mock<ISeriesCatalogRepository>().Object, new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object, activity.Object, new ConfigurationBuilder().Build(), TimeProvider.System,
            new EmptyListClientFactory(listStatus), NullLogger<IntakeSyncService>.Instance, titleTagService: tags.Object);
        return (service, intake, tags, activity);
    }

    [Fact]
    public async Task With_the_setting_on_a_title_that_left_every_list_gets_the_chosen_tag_and_activity_says_so()
    {
        var (service, _, tags, activity) = Build(Settings(true, "off-list"), [new IntakeDroppedTitle("movies", "movie-solo", "Solo"), new IntakeDroppedTitle("tv", "series-gone", "Gone Show")]);

        var result = await service.RunAsync("source-watchlist", relatedJobId: null, manual: false, CancellationToken.None);

        Assert.Equal("success", result.Status);
        tags.Verify(t => t.GiveAsync(MediaKind.Movie, "movie-solo", It.Is<IEnumerable<string>>(n => n.SequenceEqual(new[] { "off-list" })), It.IsAny<CancellationToken>()), Times.Once);
        tags.Verify(t => t.GiveAsync(MediaKind.Series, "series-gone", It.Is<IEnumerable<string>>(n => n.SequenceEqual(new[] { "off-list" })), It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(a => a.RecordActivityAsync("intake.off-list-tagged",
            It.Is<string>(m => m.Contains("no longer on any list") && m.Contains("off-list") && m.Contains("Solo") && m.Contains("Gone Show")),
            It.IsAny<string?>(), It.IsAny<string?>(), "intake-source", "source-watchlist", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task With_the_setting_off_nothing_is_marked_or_tagged()
    {
        var (service, intake, tags, _) = Build(Settings(false, "off-list"), [new IntakeDroppedTitle("movies", "movie-solo", "Solo")]);

        await service.RunAsync("source-watchlist", relatedJobId: null, manual: false, CancellationToken.None);

        intake.Verify(repo => repo.MarkDroppedTitlesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task With_the_setting_on_but_no_tag_chosen_nothing_is_marked_or_tagged()
    {
        var (service, intake, tags, _) = Build(Settings(true, "  "), [new IntakeDroppedTitle("movies", "movie-solo", "Solo")]);

        await service.RunAsync("source-watchlist", relatedJobId: null, manual: false, CancellationToken.None);

        intake.Verify(repo => repo.MarkDroppedTitlesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_list_that_could_not_be_fetched_never_makes_a_title_look_as_if_it_left()
    {
        var (service, intake, tags, _) = Build(Settings(true, "off-list"), [new IntakeDroppedTitle("movies", "movie-solo", "Solo")], HttpStatusCode.InternalServerError);

        var result = await service.RunAsync("source-watchlist", relatedJobId: null, manual: false, CancellationToken.None);

        Assert.Equal("error", result.Status);
        intake.Verify(repo => repo.MarkDroppedTitlesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        tags.Verify(t => t.GiveAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class EmptyListClientFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("\n", Encoding.UTF8, "text/plain") });
        }
    }
}
