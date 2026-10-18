using System.Runtime.CompilerServices;
using Deluno.Api.IndexerHistory;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #587: a release that appears on an indexer is searched for straight away,
/// instead of waiting for its title's turn in the search cycle.
///
/// <para><b>Why this file exists.</b> On the soak rig the TV cycle spent every
/// slot on ten episodes of a show with hundreds that will never be released,
/// and the Breaking Bad episodes the indexer had just published were never
/// looked for. Radarr and Sonarr read the feed every fifteen minutes.</para>
/// </summary>
public sealed class RssSyncTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");

    private readonly Mock<IConnectionsRepository> _connections = new();
    private readonly Mock<ILibrariesRepository> _libraries = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IIndexerFeedReader> _feed = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly List<EnqueueJobRequest> _queued = [];
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private IndexerFeedResult _answer = new(true, []);
    private readonly Mock<Deluno.Contracts.IMediaTagStore> _tags = new();
    private LibraryItem _movieLibrary = Library("lib-movies", "Movies", "movies");

    public RssSyncTests()
    {
        _libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [_movieLibrary, Library("lib-tv", "TV", "tv")]);
        _libraries.Setup(repo => repo.GetLibraryRoutingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new LibraryRoutingSnapshot(id, id, [Blank<LibrarySourceLinkItem>() with { IndexerId = "idx-1" }], []));
        _connections.Setup(repo => repo.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Blank<IndexerItem>() with { Id = "idx-1", Name = "Soak Torznab", IsEnabled = true, RssEnabled = true, AutomaticSearchEnabled = true }]);
        // As the real catalogue list reads: no library and no wanted status on
        // it. Those come from each library's wanted list, which lists only
        // what is searchable (Sintel is held, so it is not there).
        _movies.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Movie("m-general", "The General", 1926),
                Movie("m-sintel", "Sintel", 2010),
                Movie("m-cleaner", "Cleaner", 2007, monitored: false),
                Movie("m-brain", "The Brain That Wouldn't Die", 1962)
            ]);
        _movies.Setup(repo => repo.ListEligibleWantedAsync("lib-movies", It.IsAny<int>(), It.IsAny<DateTimeOffset>(), true,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([
                Wanted("m-general", "The General", 1926, "missing"),
                Wanted("m-cleaner", "Cleaner", 2007, "upgrade"),
                Wanted("m-brain", "The Brain That Wouldn't Die", 1962, "missing")
            ]);
        _series.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Blank<SeriesListItem>() with { Id = "s-bb", Title = "Breaking Bad", StartYear = 2008, Monitored = true }]);
        _series.Setup(repo => repo.ListEligibleWantedAsync("lib-tv", It.IsAny<int>(), It.IsAny<DateTimeOffset>(), true,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([Blank<SeriesWantedItem>() with { SeriesId = "s-bb", Title = "Breaking Bad", StartYear = 2008, LibraryId = "lib-tv", WantedStatus = "missing" }]);
        _series.Setup(repo => repo.GetInventoryDetailAsync("s-bb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesInventoryDetail("s-bb", "Breaking Bad", 2008, 2, 3, 1,
                [Episode("e-102", 1, 2, "missing"), Episode("e-101", 1, 1, "covered"), Episode("e-201", 2, 1, "missing")], null));
        _feed.Setup(reader => reader.ReadRecentAsync(It.IsAny<IndexerItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _answer);
        _scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => _queued.Add(request))
            .ReturnsAsync((JobQueueItem)null!);
    }

    [Fact]
    public async Task A_new_release_for_a_wanted_film_searches_for_that_film_now()
    {
        Feed("The.General.1926.1080p.BluRay.DTS-HD.MA.5.1.x264-DELUNO");

        await Sync();

        var request = Assert.Single(_queued);
        Assert.Equal(LibrarySearchJobTypes.Movies, request.JobType);
        Assert.Equal("m-general", request.RelatedEntityId);
        Assert.Contains("\"targetEntityId\":\"m-general\"", request.PayloadJson);
        Assert.Contains("\"triggeredBy\":\"rss\"", request.PayloadJson);
        // The indexer whose release started the search is the one the search waits for (#1184).
        Assert.Contains("\"priorityIndexerIds\":[\"idx-1\"]", request.PayloadJson);
    }

    [Fact]
    public async Task A_new_release_for_a_wanted_episode_searches_for_that_episode_now()
    {
        Feed("Breaking.Bad.S01E02.720p.WEB-DL.DDP5.1.H.264-DELUNO", "Breaking.Bad.S02E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO");

        await Sync();

        Assert.Equal(new[] { "e-102", "e-201" }, _queued.Select(request => request.RelatedEntityId));
        Assert.All(_queued, request => Assert.Equal("episode.search", request.JobType));
        Assert.All(_queued, request => Assert.Contains("\"priorityIndexerIds\":[\"idx-1\"]", request.PayloadJson));
        // One search of an episode at a time, whichever pass asked.
        Assert.Equal("episode.search:e-102", _queued[0].DedupeKey);
    }

    [Theory]
    [InlineData("Sintel.2010.1080p.WEB-DL-DELUNO")]                 // covered: nothing wanted
    [InlineData("Cleaner.2007.1080p.BluRay.x264-DELUNO")]           // not monitored
    [InlineData("Breaking.Bad.S01E01.1080p.WEB-DL-DELUNO")]         // episode already held
    [InlineData("The.General.1927.1080p.WEB-DL-DELUNO")]            // another year
    [InlineData("Something.Else.Entirely.2020.1080p.WEB-DL-GROUP")] // not in the library
    public async Task A_release_for_nothing_wanted_searches_for_nothing(string release)
    {
        Feed(release);

        await Sync();

        Assert.Empty(_queued);
    }

    [Fact]
    public async Task A_title_is_matched_however_its_apostrophe_is_spelled()
    {
        Feed("The.Brain.That.Wouldnt.Die.1962.1080p.WEB-DL.DDP5.1.H.264-DELUNO");

        await Sync();

        Assert.Equal("m-brain", Assert.Single(_queued).RelatedEntityId);
    }

    /// <summary>
    /// A feed keeps listing a release for days. Acted on every pass, a release
    /// held for review would be searched for every fifteen minutes.
    /// </summary>
    [Fact]
    public async Task A_release_is_acted_on_once_not_on_every_pass()
    {
        Feed("The.General.1926.1080p.BluRay.DTS-HD.MA.5.1.x264-DELUNO");
        await Sync();
        await Sync();

        Assert.Single(_queued);
    }

    /// <summary>#1354: the library's "Only for titles tagged" and "Never for titles tagged" choices for the indexer hold in RSS too.</summary>
    [Theory]
    [InlineData("kids", "", new string[0], false)]
    [InlineData("kids", "", new[] { "kids" }, true)]
    [InlineData("", "horror", new[] { "horror" }, false)]
    [InlineData("", "horror", new[] { "kids" }, true)]
    [InlineData("kids", "horror", new[] { "kids", "horror" }, false)]
    public async Task A_new_release_searches_for_a_film_only_when_the_librarys_tag_choices_for_that_indexer_allow_it(string only, string never, string[] filmTags, bool searched)
    {
        _libraries.Setup(repo => repo.GetLibraryRoutingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new LibraryRoutingSnapshot(id, id, [Blank<LibrarySourceLinkItem>() with { IndexerId = "idx-1", RequiredTags = only, ExcludedTags = never }], []));
        _tags.Setup(store => store.ListAllTitleTagsAsync(Deluno.Contracts.MediaKind.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Deluno.Contracts.MediaTitleTags("m-general", "The General", true, filmTags)]);
        _tags.Setup(store => store.ListAllTitleTagsAsync(Deluno.Contracts.MediaKind.Series, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Feed("The.General.1926.1080p.BluRay.DTS-HD.MA.5.1.x264-DELUNO");

        await Sync();

        Assert.Equal(searched ? 1 : 0, _queued.Count(request => request.RelatedEntityId == "m-general"));
    }

    [Fact]
    public async Task A_new_episode_release_is_left_alone_when_the_librarys_never_for_tag_is_on_the_show()
    {
        _libraries.Setup(repo => repo.GetLibraryRoutingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new LibraryRoutingSnapshot(id, id, [Blank<LibrarySourceLinkItem>() with { IndexerId = "idx-1", RequiredTags = "", ExcludedTags = "crime" }], []));
        _tags.Setup(store => store.ListAllTitleTagsAsync(Deluno.Contracts.MediaKind.Movie, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tags.Setup(store => store.ListAllTitleTagsAsync(Deluno.Contracts.MediaKind.Series, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Deluno.Contracts.MediaTitleTags("s-bb", "Breaking Bad", true, ["crime"])]);
        Feed("Breaking.Bad.S01E02.720p.WEB-DL.DDP5.1.H.264-DELUNO");

        await Sync();

        Assert.Empty(_queued);
    }

    [Fact]
    public async Task A_feed_that_could_not_be_read_keeps_its_place()
    {
        _lastSeen["idx-1"] = Now.AddHours(-1);
        _answer = new IndexerFeedResult(false, [], "Soak Torznab did not answer.");

        await Sync();

        Assert.Equal(Now.AddHours(-1), _lastSeen["idx-1"]);
    }

    [Fact]
    public async Task A_library_with_automatic_searching_off_is_left_alone()
    {
        _movieLibrary = _movieLibrary with { AutoSearchEnabled = false };
        Feed("The.General.1926.1080p.BluRay.DTS-HD.MA.5.1.x264-DELUNO");

        await Sync();

        Assert.Empty(_queued);
    }

    [Theory]
    [InlineData("The.General.1926.1080p.BluRay.x264-DELUNO", "the general", 1926, null, null)]
    [InlineData("2001.A.Space.Odyssey.1968.2160p.UHD.BluRay-GRP", "2001 a space odyssey", 1968, null, null)]
    [InlineData("Breaking.Bad.S02E01.1080p.WEB-DL-DELUNO", "breaking bad", null, 2, 1)]
    [InlineData("Doctor.Who.2005.S01E01.720p.HDTV-GRP", "doctor who 2005", null, 1, 1)]
    public void A_release_name_says_which_title_it_is_for(string release, string title, int? year, int? season, int? episode)
    {
        var parsed = FeedReleaseNames.Parse(release);

        Assert.NotNull(parsed);
        Assert.Equal(title, FeedReleaseNames.NormalizeTitle(parsed!.Title));
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    private void Feed(params string[] releases)
        => _answer = new IndexerFeedResult(true, releases.Select(name => new IndexerFeedRelease(name, Now.AddMinutes(-5))).ToArray());

    private async Task Sync()
    {
        var state = new Mock<IRssSyncStateRepository>();
        state.Setup(repo => repo.GetLastSeenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<string, DateTimeOffset>(_lastSeen));
        state.Setup(repo => repo.SaveLastSeenAsync(It.IsAny<IReadOnlyDictionary<string, DateTimeOffset>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<string, DateTimeOffset>, CancellationToken>((saved, _) =>
            {
                _lastSeen.Clear();
                foreach (var pair in saved) _lastSeen[pair.Key] = pair.Value;
            })
            .Returns(Task.CompletedTask);
        await new RssSyncService(
            _connections.Object, _libraries.Object, _movies.Object, _series.Object, _feed.Object, state.Object,
            _scheduler.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IIndexerHistoryEventStore>(), new Clock(Now), NullLogger<RssSyncService>.Instance, _tags.Object)
            .SyncAsync(CancellationToken.None);
    }

    private static LibraryItem Library(string id, string name, string mediaType)
        => Blank<LibraryItem>() with { Id = id, Name = name, MediaType = mediaType, AutoSearchEnabled = true, RetryDelayHours = 1 };

    private static MovieListItem Movie(string id, string title, int year, bool monitored = true)
        => Blank<MovieListItem>() with { Id = id, Title = title, ReleaseYear = year, Monitored = monitored };

    private static MovieWantedItem Wanted(string id, string title, int year, string wanted)
        => Blank<MovieWantedItem>() with { MovieId = id, Title = title, ReleaseYear = year, LibraryId = "lib-movies", WantedStatus = wanted };

    private static SeriesEpisodeInventoryItem Episode(string id, int season, int episode, string wanted)
        => Blank<SeriesEpisodeInventoryItem>() with { EpisodeId = id, SeasonNumber = season, EpisodeNumber = episode, Monitored = true, WantedStatus = wanted };

    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
