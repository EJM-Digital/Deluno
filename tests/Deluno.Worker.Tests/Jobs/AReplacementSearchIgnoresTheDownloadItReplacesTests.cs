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
/// #1482: download health struck out four dead torrents on the rig and queued
/// each film's replacement search before the struck download was removed and
/// archived. The search ran at once, the struck dispatch was still listed as in
/// flight, the film kept its downloading mark, was not a candidate, and the
/// search planned 0. The search now ignores the dispatch it was queued for, so
/// the order of remove, archive and queue does not matter.
/// </summary>
public sealed class AReplacementSearchIgnoresTheDownloadItReplacesTests
{
    private const string StruckRelease = "Kickboxer.1989.1080p.BluRay.x264-DEAD";
    private const string NextRelease = "Kickboxer.1989.1080p.WEB-DL.DDP5.1.H.264-NEXT";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T22:00:00Z");

    [Fact]
    public async Task A_film_whose_struck_download_is_still_listed_is_planned_and_the_next_release_sent()
    {
        var world = new World(entityType: "movie", entityId: "movie-1", struckDispatchId: "dispatch-1");

        await world.MoviesHandler().HandleAsync(world.Job("movies", "movie-1", "dispatch-1"), CancellationToken.None);

        world.VerifySent();
    }

    [Fact]
    public async Task A_show_whose_struck_download_is_still_listed_is_planned_and_the_next_release_sent()
    {
        var world = new World(entityType: "series", entityId: "series-1", struckDispatchId: "dispatch-1");

        await world.TvHandler().HandleAsync(world.Job("tv", "series-1", "dispatch-1"), CancellationToken.None);

        world.VerifySent();
    }

    /// <summary>Another download for the same title is still a reason to wait.</summary>
    [Fact]
    public async Task A_different_download_for_the_same_film_still_holds_the_search_back()
    {
        var world = new World(entityType: "movie", entityId: "movie-1", struckDispatchId: "dispatch-1",
            otherInFlight: new DownloadInFlight("movie", "movie-1", "Kickboxer.1989.720p", DispatchId: "dispatch-2"));

        await world.MoviesHandler().HandleAsync(world.Job("movies", "movie-1", "dispatch-1"), CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The episode search a replacement plans runs on its own, so it carries the ignored dispatch with it.</summary>
    [Theory]
    [InlineData("episode", "episode-1")]
    [InlineData("series", "series-1")]
    public async Task An_episode_search_planned_for_a_replacement_ignores_the_struck_download(string entityType, string entityId)
    {
        var world = new World(entityType: entityType, entityId: entityId, struckDispatchId: "dispatch-1");

        await world.EpisodeHandler().HandleAsync(
            TestJobs.Create("episode.search", payloadJson: JsonSerializer.Serialize(new
            {
                episodeId = "episode-1",
                seriesId = "series-1",
                libraryId = "library-1",
                seasonNumber = 1,
                episodeNumber = 1,
                title = "Pilot",
                ignoreDispatchId = "dispatch-1"
            })),
            CancellationToken.None);

        world.VerifySent();
    }

    [Fact]
    public async Task An_episode_search_with_no_ignored_dispatch_still_waits_for_the_download_on_its_way()
    {
        var world = new World(entityType: "episode", entityId: "episode-1", struckDispatchId: "dispatch-1");

        await world.EpisodeHandler().HandleAsync(
            TestJobs.Create("episode.search", payloadJson: JsonSerializer.Serialize(new
            {
                episodeId = "episode-1",
                seriesId = "series-1",
                libraryId = "library-1",
                seasonNumber = 1,
                episodeNumber = 1,
                title = "Pilot"
            })),
            CancellationToken.None);

        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_search_that_finds_no_other_release_says_so_naming_the_film()
    {
        var world = new World(entityType: "movie", entityId: "movie-1", struckDispatchId: "dispatch-1", findsRelease: false);

        await world.MoviesHandler().HandleAsync(world.Job("movies", "movie-1", "dispatch-1"), CancellationToken.None);

        var message = Assert.Single(world.Activity, entry => entry.Kind == "library.search.executed").Message;
        Assert.Contains("Kickboxer", message);
        Assert.Contains("no other usable release", message);
        Assert.DoesNotContain("nothing else to look for", message);
    }

    [Fact]
    public async Task A_search_that_finds_no_other_release_says_so_naming_the_show()
    {
        var world = new World(entityType: "series", entityId: "series-1", struckDispatchId: "dispatch-1", findsRelease: false);

        await world.TvHandler().HandleAsync(world.Job("tv", "series-1", "dispatch-1"), CancellationToken.None);

        var message = Assert.Single(world.Activity, entry => entry.Kind == "library.search.executed").Message;
        Assert.Contains("Pioneer One", message);
        Assert.Contains("no other usable release", message);
    }

    private sealed class World
    {
        private bool _marked = true;

        public List<(string Kind, string Message)> Activity { get; } = [];
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();

        private readonly Mock<ILibrariesRepository> _libraries = new();
        private readonly Mock<IJobQueueRepository> _jobs = new();
        private readonly Mock<IMovieCatalogRepository> _movies = new();
        private readonly Mock<ISeriesCatalogRepository> _series = new();
        private readonly Mock<IAcquisitionDecisionPipeline> _pipeline = new();
        private readonly Mock<IMediaStateRepository> _mediaState = new();
        private readonly Mock<IActivityFeedRepository> _activity = new();

        public World(string entityType, string entityId, string struckDispatchId, bool findsRelease = true, DownloadInFlight? otherInFlight = null)
        {
            var inFlight = new List<DownloadInFlight> { new(entityType, entityId, StruckRelease, DispatchId: struckDispatchId) };
            if (otherInFlight is not null)
            {
                inFlight.Add(otherInFlight);
            }

            _libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var source = new LibrarySourceLinkItem("source-link", "library-1", "indexer-1", "Fixture", 1, "", "", Now, Now);
            var clientLink = new LibraryDownloadClientLinkItem("link-1", "library-1", "client-1", "qBittorrent", 1, Now, Now);
            _libraries.Setup(repository => repository.GetLibraryRoutingAsync("library-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryRoutingSnapshot("library-1", "Library", [source], [clientLink]));
            _jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(inFlight);

            // A title marked downloading is not a candidate; the search only releases the mark when nothing else is in flight.
            _mediaState
                .Setup(state => state.SetDownloadingAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Callback<MediaKind, string, bool, DateTimeOffset, CancellationToken>((_, _, downloading, _, _) => _marked = downloading)
                .Returns(Task.CompletedTask);
            _movies.Setup(repository => repository.ListEligibleWantedAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync(() => _marked || entityType != "movie"
                    ? []
                    : [new MovieWantedItem("movie-1", "Kickboxer", 1989, null, "library-1", "missing", "missing",
                        false, null, "WEB 1080p", false, Now, null, null, null, false, null, Now)]);
            _series.Setup(repository => repository.ListEligibleWantedAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync(() => _marked || entityType != "series"
                    ? []
                    : [new SeriesWantedItem("series-1", "Pioneer One", 2010, null, "library-1", "missing", "missing",
                        false, null, "WEB 1080p", false, false, null, Now, null, null, null, Now)]);
            _series.Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
                .ReturnsAsync([]);

            var candidate = new MediaSearchCandidate(NextRelease, "indexer-1", "Fixture", "WEB 1080p", 100, true, "Best.", DownloadUrl: "http://fixture.invalid/release.torrent");
            var link = new LibraryDownloadClientLinkItem("link-1", "library-1", "client-1", "qBittorrent", 1, Now, Now);
            var plan = findsRelease
                ? new AcquisitionDecisionPlan(
                    new MediaSearchPlan(candidate, [candidate], "Found one."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion, "matched", "Found one.", 1, 1, link,
                    true,
                    new DownloadClientGrabRequest(NextRelease, "http://fixture.invalid/release.torrent", "movies", "movies", "Fixture"),
                    [])
                : new AcquisitionDecisionPlan(
                    new MediaSearchPlan(null, [], "Nothing usable."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion, "no-match", "Nothing usable.", 1, 1, link,
                    false, null, []);
            _pipeline.Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(plan);
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientGrabResult("client-1", NextRelease, true, "sent", "sent") { ExternalId = "hash-1" });
            _activity
                .Setup(feed => feed.RecordActivityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, CancellationToken>((kind, message, _, _, _, _, _) => Activity.Add((kind, message)))
                .ReturnsAsync((ActivityEventItem)null!);
        }

        public JobQueueItem Job(string mediaType, string target, string dispatchId)
            => TestJobs.Create(
                LibrarySearchJobTypes.For(mediaType),
                payloadJson: JsonSerializer.Serialize(new
                {
                    libraryId = "library-1",
                    libraryName = mediaType == "movies" ? "Movies" : "TV",
                    mediaType,
                    checkMissing = true,
                    checkUpgrades = true,
                    maxItems = 1,
                    retryDelayHours = 24,
                    triggeredBy = "health-replacement",
                    targetEntityId = target
                }),
                relatedEntityId: dispatchId,
                relatedEntityType: "download_dispatch");

        public void VerifySent()
            => Grabs.Verify(grab => grab.GrabAsync("client-1", It.Is<DownloadClientGrabRequest>(request => request.ReleaseName == NextRelease), It.IsAny<CancellationToken>()), Times.Once);

        public MoviesLibrarySearchJobHandler MoviesHandler()
            => new(_libraries.Object, new Mock<IQualityRepository>().Object, _jobs.Object, _movies.Object, _series.Object,
                _pipeline.Object, Grabs.Object, _activity.Object, new FixedClock(Now),
                mediaStateRepository: _mediaState.Object);

        public EpisodeSearchJobHandler EpisodeHandler()
            => new(_libraries.Object, new Mock<IQualityRepository>().Object, _series.Object, _jobs.Object,
                _pipeline.Object, Grabs.Object, _activity.Object, new FixedClock(Now),
                mediaStateRepository: _mediaState.Object);

        public TvLibrarySearchJobHandler TvHandler()
            => new(_libraries.Object, new Mock<IQualityRepository>().Object, _jobs.Object, _movies.Object, _series.Object,
                _pipeline.Object, Grabs.Object, _activity.Object, new FixedClock(Now),
                mediaStateRepository: _mediaState.Object);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
