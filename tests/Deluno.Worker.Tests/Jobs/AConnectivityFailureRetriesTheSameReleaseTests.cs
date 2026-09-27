using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #875, cleanup sheet B1: the download client being offline, refusing the
/// password, or timing out is Deluno's own setup problem, never the
/// release's fault (#880, #901). Today Deluno records the failed send and
/// waits for the title's next ordinary search cycle, which may not come
/// around for a day. It now schedules another try at the exact same title a
/// short while later instead, and never blocklists anything over it.
/// </summary>
public sealed class AConnectivityFailureRetriesTheSameReleaseTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Theory]
    [InlineData(IntegrationFailureKind.Unavailable)]
    [InlineData(IntegrationFailureKind.Timeout)]
    [InlineData(IntegrationFailureKind.Authentication)]
    public async Task A_connectivity_or_setup_failure_schedules_a_retry_for_a_movie(IntegrationFailureKind kind)
    {
        var world = new World(kind);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request =>
                    request.RelatedEntityType == "movie" &&
                    request.RelatedEntityId == "movie-1" &&
                    request.ScheduledUtc > Now),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The retry must resend the exact same release to the exact same
    /// client — not a fresh title search that could pick a different
    /// release entirely.
    /// </summary>
    [Fact]
    public async Task The_scheduled_retry_carries_the_same_release_download_url_and_client()
    {
        var world = new World(IntegrationFailureKind.Unavailable);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request =>
                    request.JobType == "download-client.retry-grab" &&
                    request.PayloadJson != null &&
                    request.PayloadJson.Contains(Release) &&
                    request.PayloadJson.Contains("http://fixture.invalid/release.nzb") &&
                    request.PayloadJson.Contains("client-1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_connectivity_failure_schedules_a_retry_for_a_series()
    {
        var world = new World(IntegrationFailureKind.Unavailable);

        await world.TvHandler().HandleAsync(LibraryJob("tv"), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.RelatedEntityType == "series" && request.RelatedEntityId == "series-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_connectivity_failure_schedules_a_retry_for_an_episode()
    {
        var world = new World(IntegrationFailureKind.Unavailable);

        await world.EpisodeHandler().HandleAsync(EpisodeJob(), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.RelatedEntityType == "episode" && request.RelatedEntityId == "episode-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_connectivity_failure_is_never_blocklisted()
    {
        var world = new World(IntegrationFailureKind.Unavailable);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Every automatic action here is written to Activity in plain words.</summary>
    [Fact]
    public async Task A_connectivity_failure_is_written_to_activity_in_plain_words()
    {
        var world = new World(IntegrationFailureKind.Unavailable);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.client.connectivity-retry-scheduled",
                It.Is<string>(message => message.Contains(Release) && message.Contains("setup problem", StringComparison.OrdinalIgnoreCase)),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A dead release (#876) is a different failure and must never schedule a
    /// connectivity retry — it still queues its own replacement search
    /// straight away instead.
    /// </summary>
    [Fact]
    public async Task A_dead_release_does_not_schedule_a_connectivity_retry()
    {
        var world = new World(IntegrationFailureKind.RejectedAction, status: DownloadQueueStatuses.DeadRelease);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.PayloadJson != null && request.PayloadJson.Contains("\"AttemptNumber\"")),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_successful_grab_does_not_schedule_a_connectivity_retry()
    {
        var world = new World(null, status: "sent");

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static JobQueueItem LibraryJob(string mediaType)
        => TestJobs.Create("library.search", payloadJson: JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = mediaType == "movies" ? "Movies" : "TV",
            mediaType,
            checkMissing = true,
            checkUpgrades = false,
            searchKind = "missing",
            maxItems = 25,
            retryDelayHours = 12,
            triggeredBy = "schedule"
        }));

    private static JobQueueItem EpisodeJob()
        => TestJobs.Create("episode.search", payloadJson: JsonSerializer.Serialize(new
        {
            episodeId = "episode-1",
            seriesId = "series-1",
            libraryId = "library-1",
            seasonNumber = 1,
            episodeNumber = 1,
            title = "Pilot"
        }));

    private sealed class World
    {
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();
        public Mock<IAcquisitionDecisionPipeline> Pipeline { get; } = new();
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();
        public Mock<IActivityFeedRepository> ActivityFeed { get; } = new();
        public Mock<IBlockedReleaseRepository> BlockedReleases { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();

        public World(IntegrationFailureKind? failureKind, string status = "failed")
        {
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            BlockedReleases.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            BlockedReleases.Setup(repository => repository.ListForAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => TestJobs.Create(request.JobType, request.PayloadJson));

            Movies.Setup(repository => repository.ListEligibleWantedAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync([new MovieWantedItem("movie-1", "Tears of Steel", 2012, null, "library-1", "missing", "missing",
                    false, null, "WEB 1080p", false, Now, null, null, null, false, null, Now)]);
            Series.Setup(repository => repository.ListEligibleWantedAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync([new SeriesWantedItem("series-1", "Pioneer One", 2010, null, "library-1", "missing", "missing",
                    false, null, "WEB 1080p", false, false, null, Now, null, null, null, Now)]);
            Series.Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync([]);

            var candidate = new MediaSearchCandidate(Release, "indexer-1", "Fixture", "WEB 1080p", 100, true, "Best.", DownloadUrl: "http://fixture.invalid/release.nzb");
            Pipeline.Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AcquisitionDecisionPlan(
                    new MediaSearchPlan(candidate, [candidate], "Found one."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                    "matched",
                    "Found one.",
                    1,
                    1,
                    new LibraryDownloadClientLinkItem("link-1", "library-1", "client-1", "SABnzbd", 1, Now, Now),
                    true,
                    new DownloadClientGrabRequest(Release, "http://fixture.invalid/release.nzb", "movies", "movies", "Fixture"),
                    []));

            var result = new DownloadClientGrabResult("client-1", Release, status == "sent", status,
                status == "sent" ? "Sent." : "SABnzbd could not be reached.")
            {
                ExternalId = status == "sent" ? "nzo-1" : null,
                Failure = failureKind is { } kind
                    ? IntegrationFailureFactory.FromLegacy("download-client", "client-1", "SABnzbd", "grab", null, "SABnzbd could not be reached.") with { Kind = kind }
                    : null
            };
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }

        public MoviesLibrarySearchJobHandler MoviesHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: Scheduler.Object);

        public TvLibrarySearchJobHandler TvHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: Scheduler.Object);

        public EpisodeSearchJobHandler EpisodeHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Series.Object, Jobs.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: Scheduler.Object);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
