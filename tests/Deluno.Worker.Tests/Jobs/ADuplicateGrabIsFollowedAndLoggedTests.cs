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
/// #877, cleanup sheet B3: a torrent client saying it already holds a
/// release is nobody's fault, and Deluno follows the copy it has instead of
/// sending another. That is still an automatic decision, so it must reach
/// Activity in plain words rather than looking exactly like an ordinary
/// "sent" dispatch.
/// </summary>
public sealed class ADuplicateGrabIsFollowedAndLoggedTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Fact]
    public async Task A_duplicate_follow_is_written_to_activity_in_plain_words_for_a_movie()
    {
        var world = new World();

        await world.MoviesHandler().HandleAsync(LibraryJob(), CancellationToken.None);

        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.grab.duplicate",
                It.Is<string>(message => message.Contains("already holds this release", StringComparison.OrdinalIgnoreCase)),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_duplicate_follow_never_schedules_a_connectivity_retry_or_blocklists()
    {
        var world = new World();

        await world.MoviesHandler().HandleAsync(LibraryJob(), CancellationToken.None);

        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.BlockedReleases.Verify(repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static JobQueueItem LibraryJob()
        => TestJobs.Create("library.search", payloadJson: JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = false,
            searchKind = "missing",
            maxItems = 25,
            retryDelayHours = 12,
            triggeredBy = "schedule"
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

        public World()
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
                .ReturnsAsync([]);
            Series.Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync([]);

            var candidate = new MediaSearchCandidate(Release, "indexer-1", "Fixture", "WEB 1080p", 100, true, "Best.", DownloadUrl: "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01");
            Pipeline.Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AcquisitionDecisionPlan(
                    new MediaSearchPlan(candidate, [candidate], "Found one."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                    "matched",
                    "Found one.",
                    1,
                    1,
                    new LibraryDownloadClientLinkItem("link-1", "library-1", "client-1", "qBittorrent", 1, Now, Now),
                    true,
                    new DownloadClientGrabRequest(Release, candidate.DownloadUrl!, "movies", "movies", "Fixture"),
                    []));

            var result = new DownloadClientGrabResult("client-1", Release, true, "sent",
                "qBittorrent already holds this release. Deluno is following the copy it has instead of sending another.")
            {
                ExternalId = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                IsDuplicateFollow = true
            };
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }

        public MoviesLibrarySearchJobHandler MoviesHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: Scheduler.Object);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
