using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1192: a film added with "Search for it as soon as it is added" was sent to SABnzbd, and a minute
/// later a second search sent the same NZB to NZBGet. The look ("is it already on its way?") and the
/// recorded send were two steps, so two searches running together could each look before either had
/// written down what it sent. They now share one gate per title and release.
/// </summary>
public sealed class ASecondSendOfOneReleaseIsRefusedTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-GOLDEN";
    private const string MovieId = "movie-bbb";

    /// <summary>One search's turn: take the gate, look, and send only if nothing is on its way (the shape every search path has).</summary>
    private static async Task<bool> SearchAsync(World world, string clientId, string clientName, TimeSpan grabTakes)
    {
        using var gate = await DispatchSendGate.EnterAsync(Release, [MovieId], CancellationToken.None);
        var onItsWay = await DispatchCoverage.AlreadyOnItsWayAsync(world.Store, Release, [MovieId], CancellationToken.None);
        if (onItsWay is not null)
        {
            return false;
        }

        // Sending to the client takes a moment; this is the window the race lived in.
        await Task.Delay(grabTakes);
        await RecordAsync(world, clientId, clientName);
        return true;
    }

    private static Task RecordAsync(World world, string clientId, string clientName)
        => world.Store.RecordDownloadDispatchAsync(
            "movies", "movies", "movie", MovieId, Release, "Golden Newznab stub", clientId, clientName, "sent", null,
            grabResponseCode: 200, cancellationToken: CancellationToken.None);

    [Fact]
    public async Task Two_searches_racing_for_one_title_send_it_once()
    {
        using var world = await World.CreateAsync();

        var results = await Task.WhenAll(
            SearchAsync(world, "sab", "SABnzbd", TimeSpan.FromMilliseconds(150)),
            SearchAsync(world, "nzbget", "NZBGet", TimeSpan.FromMilliseconds(150)),
            SearchAsync(world, "sab", "SABnzbd", TimeSpan.FromMilliseconds(150)));

        Assert.Equal(1, results.Count(sent => sent));
        Assert.Single(await world.Store.ListDownloadsInFlightAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task Without_the_gate_the_same_race_sends_twice_which_is_what_the_gate_is_for()
    {
        using var world = await World.CreateAsync();

        async Task<bool> Ungated(string clientId, string clientName)
        {
            var onItsWay = await DispatchCoverage.AlreadyOnItsWayAsync(world.Store, Release, [MovieId], CancellationToken.None);
            if (onItsWay is not null)
            {
                return false;
            }

            await Task.Delay(150);
            await RecordAsync(world, clientId, clientName);
            return true;
        }

        var results = await Task.WhenAll(Ungated("sab", "SABnzbd"), Ungated("nzbget", "NZBGet"));

        Assert.Equal(2, results.Count(sent => sent));
    }

    [Fact]
    public async Task A_search_a_minute_later_finds_the_first_send_and_sends_nothing()
    {
        using var world = await World.CreateAsync();

        Assert.True(await SearchAsync(world, "sab", "SABnzbd", TimeSpan.Zero));
        Assert.False(await SearchAsync(world, "nzbget", "NZBGet", TimeSpan.Zero));

        var only = Assert.Single(await world.Store.ListDownloadsInFlightAsync(100, CancellationToken.None));
        Assert.Equal("SABnzbd", only.ClientName);
    }

    [Fact]
    public async Task A_title_whose_library_file_came_from_the_release_is_not_sent_it_again()
    {
        using var world = await World.CreateAsync();
        var id = await world.Store.RecordDownloadDispatchAsync(
            "movies", "movies", "movie", MovieId, Release, "Golden Newznab stub", "sab", "SABnzbd", "sent", null,
            grabResponseCode: 200, cancellationToken: CancellationToken.None);
        await world.Dispatches.RecordImportOutcomeAsync(id, "imported", "/media/Movies/Big Buck Bunny (2008).mkv", null, null, CancellationToken.None);

        // Nothing is in flight any more: the import finished. The title still holds the file (removed and restored).
        Assert.Empty(await world.Store.ListDownloadsInFlightAsync(100, CancellationToken.None));
        var held = await DispatchCoverage.AlreadyInLibraryAsync(world.Store, Release.ToUpperInvariant(), MovieId, titleHasFile: true, CancellationToken.None);
        Assert.Equal($"Not sent again: {Release.ToUpperInvariant()} is already in the library.", held);

        // Without a file there is a reason to fetch it again, and another release or title is never held.
        Assert.Null(await DispatchCoverage.AlreadyInLibraryAsync(world.Store, Release, MovieId, titleHasFile: false, CancellationToken.None));
        Assert.Null(await DispatchCoverage.AlreadyInLibraryAsync(world.Store, "Big.Buck.Bunny.2008.2160p.WEB-DL-OTHER", MovieId, titleHasFile: true, CancellationToken.None));
        Assert.Null(await DispatchCoverage.AlreadyInLibraryAsync(world.Store, Release, "movie-other", titleHasFile: true, CancellationToken.None));
    }

    [Fact]
    public async Task Another_title_is_never_held_up_by_the_gate()
    {
        using var first = await DispatchSendGate.EnterAsync(Release, ["movie-a"], CancellationToken.None);

        using var other = await DispatchSendGate.EnterAsync("Sintel.2010.1080p", ["movie-b"], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Two_searches_that_each_need_an_episode_and_its_series_cannot_wait_on_each_other()
    {
        async Task Take(string[] ids)
        {
            using var gate = await DispatchSendGate.EnterAsync("Abbott.S01E01", ids, CancellationToken.None);
            await Task.Delay(20);
        }

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(index => Take(index % 2 == 0 ? ["episode-1", "series-1"] : ["series-1", "episode-1"])))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("movie-added", "right away, because a film was just added")]
    [InlineData("series-added", "right away, because a show was just added")]
    [InlineData("manual", "right away, because you asked")]
    [InlineData("rss", "because new releases appeared on your indexers")]
    [InlineData("schedule", "on schedule")]
    [InlineData(null, "on schedule")]
    public void Every_way_a_search_starts_is_named_in_plain_words(string? triggeredBy, string expected)
        => Assert.Equal(expected, SearchStartedWords.WhenQueued(triggeredBy));

    [Fact]
    public async Task The_add_time_search_is_not_reported_as_on_schedule_in_Activity()
    {
        using var world = await World.CreateAsync();

        await world.Store.EnqueueAsync(
            new EnqueueJobRequest(
                "library.search.movies", "movies",
                JsonSerializer.Serialize(new
                {
                    libraryId = "movies", libraryName = "Movies", mediaType = "movies",
                    checkMissing = true, checkUpgrades = true, maxItems = 1, retryDelayHours = 6,
                    triggeredBy = "movie-added", targetEntityId = MovieId
                }),
                "movie", MovieId),
            CancellationToken.None);

        var queued = (await world.Store.ListActivityAsync(20, null, null, CancellationToken.None))
            .Single(item => item.Category == "job.queued");
        Assert.Equal("Queued a library check for Movies right away, because a film was just added.", queued.Message);
        Assert.DoesNotContain("on schedule", queued.Message);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage, SqliteJobStore store, SqliteDownloadDispatchesRepository dispatches)
        {
            _storage = storage;
            Store = store;
            Dispatches = dispatches;
        }

        public SqliteJobStore Store { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-09T02:45:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            return new World(storage, new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches), dispatches);
        }

        public void Dispose() => _storage.Dispose();
    }
}
