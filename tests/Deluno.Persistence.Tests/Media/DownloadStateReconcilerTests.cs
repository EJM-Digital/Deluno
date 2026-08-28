using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// A download that stops happening gives the title back.
///
/// <para>Keeping a downloading title off the work list is what stops Deluno
/// grabbing the same release twice. It is also how the feature could quietly
/// ruin a library: a dispatch that fails, is removed from the client, or is lost
/// when the process dies leaves a title claiming to download for ever and never
/// searched again — no error, no log line, nothing on screen. The only symptom
/// is an absence, which is the hardest kind of defect this codebase has had to
/// find twice already.</para>
///
/// <para>Written against the real state repository, with only the dispatch side
/// stubbed. A test that stubbed both would be asserting that the reconciler
/// calls the methods the reconciler calls.</para>
/// </summary>
public sealed class DownloadStateReconcilerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-02T12:00:00Z");

    [Fact]
    public async Task A_title_whose_dispatch_has_gone_is_put_back_on_the_work_list()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        var (state, id) = await SeedDownloadingAsync(storage, clock);

        var cleared = await Build(state, clock, live: []).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, cleared);
        Assert.Equal(WantedStatuses.Missing, (await state.ListWantedByIdsAsync(
            MediaKind.Movie, [id], CancellationToken.None)).Single().WantedStatus);
    }

    [Fact]
    public async Task A_download_that_is_still_going_is_left_alone()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        var (state, id) = await SeedDownloadingAsync(storage, clock);

        var cleared = await Build(state, clock, live: [id]).ReconcileAsync(CancellationToken.None);

        // Clearing a live download puts the title straight back on the work list
        // and Deluno grabs the very release it is already fetching.
        Assert.Equal(0, cleared);
        Assert.Equal(WantedStatuses.Downloading, (await state.ListWantedByIdsAsync(
            MediaKind.Movie, [id], CancellationToken.None)).Single().WantedStatus);
    }

    /// <summary>
    /// A grab writes the wanted status and the dispatch row in that order, so a
    /// title caught between the two is healthy rather than abandoned. Ten
    /// minutes of grace is far longer than that gap can be.
    /// </summary>
    [Fact]
    public async Task A_title_marked_moments_ago_is_left_for_the_dispatch_to_catch_up()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        var (state, id) = await SeedDownloadingAsync(storage, clock);

        // No dispatch exists yet, which is exactly the racy instant.
        var cleared = await Build(state, clock, live: []).ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, cleared);

        // ...and with the mark placed a moment ago rather than ten minutes back,
        // it is left alone.
        await state.SetDownloadingAsync(MediaKind.Movie, id, downloading: true, Now, CancellationToken.None);
        var again = await Build(state, new FixedTimeProvider(Now.AddMinutes(1)), live: []).ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, again);
    }

    /* ------------------------------------------------------------ helpers */

    private static DownloadStateReconciler Build(
        IMediaStateRepository state,
        TimeProvider clock,
        string[] live)
        => new(state, new StubLive(live), clock, NullLogger<DownloadStateReconciler>.Instance);

    private static async Task<(IMediaStateRepository State, string Id)> SeedDownloadingAsync(
        TestStorage storage,
        TimeProvider clock)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, clock);

        await new MoviesSchemaInitializer(
            storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        // Both, because the reconciler sweeps both catalogues — a real install
        // always has the pair, and a test with only one would be asserting
        // against a shape that never ships.
        await new SeriesSchemaInitializer(
            storage.Factory, migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var state = new SqliteMediaStateRepository(storage.Factory, clock);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock, state);

        await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.Single().Id;

        await state.EnsureWantedStateAsync(
            MediaKind.Movie, id, "library-movies", WantedStatuses.Missing,
            "Not here yet.", hasFile: false, null, "WEB 1080p", qualityCutoffMet: false,
            CancellationToken.None);

        // Marked well before the grace period, so the reconciler is entitled to
        // have an opinion about it.
        await state.SetDownloadingAsync(
            MediaKind.Movie, id, downloading: true, Now.AddHours(-2), CancellationToken.None);

        return (state, id);
    }

    /// <summary>
    /// Three lines, because the reconciler asks one question. It took an
    /// <c>ILiveDownloadLookup</c> to get here: against the full dispatch
    /// repository this stub was twenty methods of NotSupportedException, none of
    /// which said anything about what is being tested.
    /// </summary>
    private sealed class StubLive(string[] live) : ILiveDownloadLookup
    {
        public Task<IReadOnlyList<string>> ListEntityIdsStillDownloadingAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(live);
    }
}
