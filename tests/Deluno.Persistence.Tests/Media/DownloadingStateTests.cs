using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// A title that is on its way says so, and comes back on its own if the
/// download never arrives.
///
/// <para><b>The second half is the point.</b> Keeping a downloading title off
/// the work list is what stops Deluno grabbing the same release twice, and it is
/// also the way this feature could quietly ruin a library: if a dispatch dies
/// and nothing rewrites the status, the title is never searched again, in
/// silence, with no error and nothing on screen. That is the shape of the two
/// worst defects this project has had — the release-search switches that starved
/// subtitles for a session, and the deleted subtitle nothing noticed.</para>
/// </summary>
public sealed class DownloadingStateTests
{
    private static readonly DateTimeOffset Monday = DateTimeOffset.Parse("2026-03-02T00:00:00Z");

    [Fact]
    public async Task A_grabbed_title_is_taken_off_the_work_list()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var (movies, state, id) = await SeedAsync(storage, clock);

        Assert.Single(await EligibleAsync(state, clock));

        await state.SetDownloadingAsync(MediaKind.Movie, id, downloading: true, clock.GetUtcNow(), CancellationToken.None);

        // Searching for it again is how you end up with two copies of the same
        // release in the download client.
        Assert.Empty(await EligibleAsync(state, clock));

        var item = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.Single();
        Assert.Equal(WantedStatuses.Downloading, item.WantedStatus);
    }

    [Fact]
    public async Task A_download_that_never_arrives_puts_the_title_back_rather_than_losing_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var (_, state, id) = await SeedAsync(storage, clock);

        await state.SetDownloadingAsync(MediaKind.Movie, id, downloading: true, clock.GetUtcNow(), CancellationToken.None);
        Assert.Empty(await EligibleAsync(state, clock));

        // Six days in it is still believed — a large torrent on a slow line
        // genuinely takes days, and firing early would mean grabbing twice.
        clock.Advance(TimeSpan.FromDays(6));
        Assert.Empty(await EligibleAsync(state, clock));

        // Past the backstop, Deluno stops believing it. Nothing failed, nothing
        // was logged, and nobody intervened — which is exactly the case this
        // exists for.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Single(await EligibleAsync(state, clock));
    }

    [Fact]
    public async Task Clearing_it_does_not_overwrite_a_title_that_already_finished()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var (_, state, id) = await SeedAsync(storage, clock);

        // The import got there first and wrote the real answer.
        await state.EnsureWantedStateAsync(
            MediaKind.Movie, id, "library-movies", WantedStatuses.Covered,
            "Imported.", hasFile: true, "WEB 1080p", "WEB 1080p", qualityCutoffMet: true,
            CancellationToken.None);

        await state.SetDownloadingAsync(MediaKind.Movie, id, downloading: false, clock.GetUtcNow(), CancellationToken.None);

        // Clearing is scoped to rows still marked downloading. Without that it
        // would take a finished film and mark it missing again.
        var still = await state.ListWantedByIdsAsync(MediaKind.Movie, [id], CancellationToken.None);
        Assert.Equal(WantedStatuses.Covered, still.Single().WantedStatus);
    }

    /// <summary>
    /// The work list is built from one list, in one place. It used to be spelled
    /// into the SQL as <c>IN ('missing', 'upgrade')</c> as well as declared in
    /// <c>IsSearchable</c>, and a status added to one and not the other fails
    /// silently in the worst direction.
    /// </summary>
    [Fact]
    public void The_searchable_statuses_and_the_predicate_cannot_disagree()
    {
        foreach (var status in WantedStatuses.All)
        {
            Assert.Equal(WantedStatuses.Searchable.Contains(status), WantedStatuses.IsSearchable(status));
        }

        Assert.False(WantedStatuses.IsSearchable(WantedStatuses.Downloading));
        Assert.False(WantedStatuses.IsSearchable(WantedStatuses.Covered));
        Assert.False(WantedStatuses.IsSearchable(WantedStatuses.Upcoming));
    }

    /* ------------------------------------------------------------ helpers */

    private static async Task<IReadOnlyList<MediaWantedItem>> EligibleAsync(
        IMediaStateRepository state,
        TimeProvider clock)
        => await state.ListEligibleWantedAsync(
            MediaKind.Movie, "library-movies", 50, clock.GetUtcNow(),
            ignoreRetryWindow: true, CancellationToken.None);

    private static async Task<(SqliteMovieCatalogRepository Movies, IMediaStateRepository State, string Id)> SeedAsync(
        TestStorage storage,
        TimeProvider clock)
    {
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var state = new SqliteMediaStateRepository(storage.Factory, clock);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock, state);

        await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.Single().Id;

        await state.EnsureWantedStateAsync(
            MediaKind.Movie, id, "library-movies", WantedStatuses.Missing,
            "Not here yet.", hasFile: false, null, "WEB 1080p", qualityCutoffMet: false,
            CancellationToken.None);

        return (movies, state, id);
    }
}
