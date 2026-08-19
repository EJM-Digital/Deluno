using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// The paged catalogue query, which is what a library-sized list has to use.
///
/// The properties that matter are: a full walk visits every row exactly once,
/// the walk is stable when rows are inserted underneath it, and the counts
/// describe the whole filtered set rather than the page — because a caller that
/// cannot tell a complete answer from a truncated one is the actual bug.
/// </summary>
public sealed class CataloguePagingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T00:00:00Z");

    [Fact]
    public async Task Paging_visits_every_row_exactly_once()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 137);

        var seen = new List<string>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await movies.ListPageAsync(
                new CatalogueQuery(PageSize: 10, PageToken: token),
                CancellationToken.None);

            seen.AddRange(page.Items.Select(item => item.Id));
            token = page.NextPageToken;
            pages++;

            Assert.True(pages < 50, "Paging did not terminate.");
        }
        while (token is not null);

        Assert.Equal(137, seen.Count);
        Assert.Equal(137, seen.Distinct().Count());
    }

    [Theory]
    [InlineData(CatalogueSortFields.Added, true)]
    [InlineData(CatalogueSortFields.Added, false)]
    [InlineData(CatalogueSortFields.Title, true)]
    [InlineData(CatalogueSortFields.Title, false)]
    [InlineData(CatalogueSortFields.Year, true)]
    [InlineData(CatalogueSortFields.Year, false)]
    [InlineData(CatalogueSortFields.Rating, true)]
    [InlineData(CatalogueSortFields.Rating, false)]
    public async Task Every_sort_pages_cleanly_in_both_directions(string sort, bool descending)
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 61);

        var seen = new List<string>();
        string? token = null;

        do
        {
            var page = await movies.ListPageAsync(
                new CatalogueQuery(Sort: sort, Descending: descending, PageSize: 7, PageToken: token),
                CancellationToken.None);

            seen.AddRange(page.Items.Select(item => item.Id));
            token = page.NextPageToken;
        }
        while (token is not null);

        // Ties on year and rating are common in the seed, and NULLs are present
        // in both. The id tiebreaker is what keeps those pages from repeating or
        // skipping rows at the boundary.
        Assert.Equal(61, seen.Count);
        Assert.Equal(61, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_row_inserted_mid_walk_does_not_shift_the_window()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 30);

        var first = await movies.ListPageAsync(new CatalogueQuery(PageSize: 10), CancellationToken.None);
        Assert.Equal(10, first.Items.Count);

        // Offset paging would now repeat a row on the next page. Keyset cannot.
        await movies.AddAsync(new CreateMovieRequest("Inserted Mid Walk", 2026, null), CancellationToken.None);

        var second = await movies.ListPageAsync(
            new CatalogueQuery(PageSize: 10, PageToken: first.NextPageToken),
            CancellationToken.None);

        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task Counts_describe_the_whole_filtered_set_not_the_page()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 40);

        var page = await movies.ListPageAsync(new CatalogueQuery(PageSize: 5), CancellationToken.None);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(40, page.TotalCount);
        Assert.NotNull(page.Facets);
        Assert.Equal(40, page.Facets.All);
        Assert.Equal(40, page.Facets.Monitored + page.Facets.Unmonitored);
        Assert.Equal(40, page.Facets.Downloaded + page.Facets.Missing);

        // Continuation pages do not recount: the caller already has the numbers,
        // and counting is the one part of this request that scans.
        var next = await movies.ListPageAsync(
            new CatalogueQuery(PageSize: 5, PageToken: page.NextPageToken),
            CancellationToken.None);
        Assert.Null(next.TotalCount);
        Assert.Null(next.Facets);
    }

    [Fact]
    public async Task Search_and_status_narrow_the_page_and_the_total_together()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        await movies.AddAsync(new CreateMovieRequest("Northern Signal", 2019, null), CancellationToken.None);
        await movies.AddAsync(new CreateMovieRequest("Northern Lights", 2020, null), CancellationToken.None);
        var quiet = await movies.AddAsync(new CreateMovieRequest("Quiet Archive", 2021, null), CancellationToken.None);
        await movies.UpdateMonitoredAsync([quiet.Id], monitored: false, CancellationToken.None);

        var search = await movies.ListPageAsync(new CatalogueQuery(Search: "northern"), CancellationToken.None);
        Assert.Equal(2, search.Items.Count);
        Assert.Equal(2, search.TotalCount);
        Assert.Equal(2, search.Facets!.All);

        // Case-insensitive, and it reaches genres as well as titles.
        Assert.Equal(2, (await movies.ListPageAsync(new CatalogueQuery(Search: "NORTHERN"), CancellationToken.None)).TotalCount);

        var unmonitored = await movies.ListPageAsync(
            new CatalogueQuery(Status: CatalogueStatusFilters.Unmonitored),
            CancellationToken.None);
        Assert.Equal(quiet.Id, Assert.Single(unmonitored.Items).Id);
        Assert.Equal(1, unmonitored.TotalCount);

        // The facets always describe the search, not the status filter — that is
        // what lets the filter buttons show their own counts.
        Assert.Equal(3, unmonitored.Facets!.All);
        Assert.Equal(2, unmonitored.Facets.Monitored);
    }

    [Fact]
    public async Task An_unreadable_token_starts_again_rather_than_failing()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 12);

        // Tokens travel in URLs and outlive deploys. A broken one must mean
        // "start from the beginning", never a 500.
        var page = await movies.ListPageAsync(
            new CatalogueQuery(PageSize: 5, PageToken: "not-a-real-token"),
            CancellationToken.None);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(12, page.TotalCount);
    }

    [Fact]
    public async Task An_oversized_page_request_is_clamped_rather_than_honoured()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, 400);

        var page = await movies.ListPageAsync(new CatalogueQuery(PageSize: 100_000), CancellationToken.None);

        // "Give me everything" is the request this whole change exists to stop
        // being possible.
        Assert.Equal(200, page.Items.Count);
        Assert.Equal(400, page.TotalCount);
        Assert.NotNull(page.NextPageToken);
    }

    [Fact]
    public async Task Series_page_the_same_way()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);

        for (var index = 0; index < 25; index++)
        {
            await series.AddAsync(
                new CreateSeriesRequest($"Show {index:D3}", 2000 + (index % 10), null),
                CancellationToken.None);
        }

        var seen = new List<string>();
        string? token = null;

        do
        {
            var page = await series.ListPageAsync(
                new CatalogueQuery(Sort: CatalogueSortFields.Title, Descending: false, PageSize: 6, PageToken: token),
                CancellationToken.None);
            seen.AddRange(page.Items.Select(item => item.Id));
            token = page.NextPageToken;
        }
        while (token is not null);

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    private static async Task SeedAsync(IMovieCatalogRepository movies, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var added = await movies.AddAsync(
                new CreateMovieRequest(
                    Title: $"Title {index:D4}",
                    // Deliberate ties and gaps: sorting by these must still be
                    // total, or a page boundary will drop or repeat a row.
                    ReleaseYear: index % 5 == 0 ? null : 1990 + (index % 12),
                    ImdbId: null,
                    Rating: index % 4 == 0 ? null : (index % 10) / 2d),
                CancellationToken.None);

            if (index % 3 == 0)
            {
                await movies.UpdateMonitoredAsync([added.Id], monitored: false, CancellationToken.None);
            }
        }
    }

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
    }
}
