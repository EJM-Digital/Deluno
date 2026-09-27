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
/// #876, cleanup sheet B2: a release the client itself refused outright at
/// grab time — a dead link, or a file it could not parse — used to report a
/// generic failed download and wait for the next ordinary search cycle, which
/// could pick the exact same bad release and fail the same way again. It is
/// blocklisted immediately instead, from every search path that can grab a
/// release, so the next search picks a different one.
///
/// <para>An ordinary connectivity failure (#875) must never take this path —
/// only <see cref="DownloadQueueStatuses.DeadRelease"/>, which a download
/// client only reports when it looked at the exact link or file and refused
/// it in the same request.</para>
/// </summary>
public sealed class ADeadOrBrokenLinkIsBlocklistedAndSearchedAgainTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Fact]
    public async Task A_dead_link_at_grab_is_blocklisted_for_a_movie()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(
                It.Is<BlockedRelease>(release =>
                    release.ReleaseName == Release &&
                    release.ReasonCode == ImportFailurePolicy.DeadOrBrokenLink &&
                    release.MediaType == "movies" &&
                    release.EntityId == "movie-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_dead_link_at_grab_is_blocklisted_for_a_series()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.TvHandler().HandleAsync(LibraryJob("tv"), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(
                It.Is<BlockedRelease>(release =>
                    release.ReasonCode == ImportFailurePolicy.DeadOrBrokenLink &&
                    release.MediaType == "tv" &&
                    release.EntityId == "series-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_dead_link_at_grab_is_blocklisted_for_an_episode()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.EpisodeHandler().HandleAsync(EpisodeJob(), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(
                It.Is<BlockedRelease>(release =>
                    release.ReasonCode == ImportFailurePolicy.DeadOrBrokenLink &&
                    release.MediaType == "tv" &&
                    release.EntityId == "episode-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #876: the acceptance is that a replacement search is queued straight
    /// away rather than left to the title's next ordinary search cycle.
    /// </summary>
    [Fact]
    public async Task A_dead_link_queues_a_replacement_search_for_a_movie()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.JobScheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request =>
                    request.JobType == "library.search.movies" &&
                    request.PayloadJson!.Contains("\"targetEntityId\":\"movie-1\"") &&
                    request.PayloadJson!.Contains("\"triggeredBy\":\"health-replacement\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_dead_link_queues_a_replacement_search_for_a_series()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.TvHandler().HandleAsync(LibraryJob("tv"), CancellationToken.None);

        world.JobScheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request =>
                    request.JobType == "library.search.tv" &&
                    request.PayloadJson!.Contains("\"targetEntityId\":\"series-1\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_dead_link_queues_a_replacement_search_for_an_episode()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.EpisodeHandler().HandleAsync(EpisodeJob(), CancellationToken.None);

        world.JobScheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request =>
                    request.JobType == "library.search.tv" &&
                    request.PayloadJson!.Contains("\"targetEntityId\":\"episode-1\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #875/#880/#901: an ordinary failure never causes an early replacement
    /// search either — it is left to the connectivity retry lane, not treated
    /// as a bad release worth searching around.
    /// </summary>
    [Fact]
    public async Task An_ordinary_grab_failure_never_queues_a_replacement_search()
    {
        var world = new World("failed");

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.JobScheduler.Verify(
            scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Every automatic action here is written to Activity in plain words.</summary>
    [Fact]
    public async Task A_dead_link_is_written_to_activity_in_plain_words()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.release.dead-link",
                It.Is<string>(message =>
                    message.Contains(Release) &&
                    message.Contains("blocklisted", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("searching for a replacement now", StringComparison.OrdinalIgnoreCase)),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #875/#880/#901: an ordinary connectivity failure is never treated as a
    /// bad release. Only the client's own "I looked at this and refused it"
    /// signal blocklists anything.
    /// </summary>
    [Fact]
    public async Task An_ordinary_grab_failure_is_never_blocklisted()
    {
        var world = new World("failed");

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_successful_grab_is_never_blocklisted()
    {
        var world = new World("sent");

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()),
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

    /// <summary>One library with one wanted title, a search that finds one release, and a client that answers with a chosen grab status.</summary>
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
        public Mock<IJobScheduler> JobScheduler { get; } = new();

        public World(string grabStatus)
        {
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            BlockedReleases.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            BlockedReleases.Setup(repository => repository.ListForAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            JobScheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
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
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientGrabResult(
                    "client-1", Release, grabStatus == "sent", grabStatus,
                    grabStatus == DownloadQueueStatuses.DeadRelease
                        ? "SABnzbd did not accept the release URL. It may be a dead link or a file it could not read."
                        : grabStatus)
                { ExternalId = "nzo-1" });
        }

        public MoviesLibrarySearchJobHandler MoviesHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: JobScheduler.Object);

        public TvLibrarySearchJobHandler TvHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: JobScheduler.Object);

        public EpisodeSearchJobHandler EpisodeHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Series.Object, Jobs.Object,
                Pipeline.Object, Grabs.Object, ActivityFeed.Object, new FixedClock(Now),
                blockedReleases: BlockedReleases.Object, jobScheduler: JobScheduler.Object);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
