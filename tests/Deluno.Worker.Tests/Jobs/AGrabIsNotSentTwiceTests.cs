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
/// An automatic grab puts its title on its way, as a manual one always did (#555).
///
/// <para>On the rig, while Weir held the first copies, the series pass grabbed
/// one TV episode five times in two days and the movie pass one film three
/// times: nothing marked the title as downloading, so when its retry window
/// passed it was searched again and the same release won.</para>
/// </summary>
public sealed class AGrabIsNotSentTwiceTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Fact]
    public async Task A_movie_the_automatic_search_sends_reads_as_downloading()
    {
        var world = new World();

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync("client-1", It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        world.MediaState.Verify(state => state.SetDownloadingAsync(MediaKind.Movie, "movie-1", true, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_series_the_automatic_search_sends_reads_as_downloading()
    {
        var world = new World();

        await world.TvHandler().HandleAsync(LibraryJob("tv"), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync("client-1", It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        world.MediaState.Verify(state => state.SetDownloadingAsync(MediaKind.Series, "series-1", true, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_grab_the_client_refused_leaves_the_title_on_the_work_list()
    {
        var world = new World(grabStatus: "failed");

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.MediaState.Verify(state => state.SetDownloadingAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), true, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A title grabbed before automatic grabs set the mark is still on the work
    /// list, and on the rig that is exactly what repeated. The download on its
    /// way is enough to stop a second.
    /// </summary>
    [Fact]
    public async Task A_title_whose_download_is_on_its_way_is_not_sent_again()
    {
        var world = new World(inFlight: [new DownloadInFlight("movie", "movie-1", Release)]);

        await world.MoviesHandler().HandleAsync(LibraryJob("movies"), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Movies.Verify(movies => movies.RecordSearchAttemptAsync(
            "movie-1", "library-1", It.IsAny<string>(), "skipped", It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.Is<string>(message => message.Contains("already on its way")), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The series and episode passes can reach the same release from their two sides.</summary>
    [Fact]
    public async Task The_series_pass_does_not_send_a_release_the_episode_pass_already_sent()
    {
        var world = new World(inFlight: [new DownloadInFlight("episode", "episode-1", Release)]);

        await world.TvHandler().HandleAsync(LibraryJob("tv"), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An episode has no downloading mark. Its own download on its way, or one
    /// for its series (a pack, or this very episode), stops a second grab.
    /// </summary>
    [Theory]
    [InlineData("episode", "episode-1", "Some.Other.Release.S01E01")]
    [InlineData("series", "series-1", "Some.Other.Release.S01")]
    public async Task An_episode_with_a_download_on_its_way_is_not_grabbed_again(string entityType, string entityId, string releaseName)
    {
        var world = new World(inFlight: [new DownloadInFlight(entityType, entityId, releaseName)]);

        await world.EpisodeHandler().HandleAsync(EpisodeJob(), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Series.Verify(series => series.RecordSearchAttemptAsync(
            "series-1", "episode-1", "library-1", "automatic", "skipped", It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_episode_with_nothing_on_its_way_is_grabbed()
    {
        var world = new World();

        await world.EpisodeHandler().HandleAsync(EpisodeJob(), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync("client-1", It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>One library with one wanted title, a search that finds one release, and a client that takes it.</summary>
    private sealed class World
    {
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();
        public Mock<IAcquisitionDecisionPipeline> Pipeline { get; } = new();
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();
        public Mock<IMediaStateRepository> MediaState { get; } = new();

        public World(string grabStatus = "sent", IReadOnlyList<DownloadInFlight>? inFlight = null)
        {
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(inFlight ?? []);

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
                .ReturnsAsync(new DownloadClientGrabResult("client-1", Release, grabStatus == "sent", grabStatus, grabStatus) { ExternalId = "nzo-1" });
        }

        public MoviesLibrarySearchJobHandler MoviesHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, new Mock<IActivityFeedRepository>().Object, new FixedClock(Now),
                mediaStateRepository: MediaState.Object);

        public TvLibrarySearchJobHandler TvHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object, Movies.Object, Series.Object,
                Pipeline.Object, Grabs.Object, new Mock<IActivityFeedRepository>().Object, new FixedClock(Now),
                mediaStateRepository: MediaState.Object);

        public EpisodeSearchJobHandler EpisodeHandler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Series.Object, Jobs.Object,
                Pipeline.Object, Grabs.Object, new Mock<IActivityFeedRepository>().Object, new FixedClock(Now),
                mediaStateRepository: MediaState.Object);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
