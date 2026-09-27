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

        // Monitoring is its own axis now, not a status. It used to be a
        // `CatalogueStatusFilters` value, which made it mutually exclusive with
        // every real state — so "missing and unmonitored" could not be asked.
        var unmonitored = await movies.ListPageAsync(
            new CatalogueQuery(Monitored: false),
            CancellationToken.None);
        Assert.Equal(quiet.Id, Assert.Single(unmonitored.Items).Id);
        Assert.Equal(1, unmonitored.TotalCount);

        // The state facets are counted inside the monitoring scope, so `All`
        // is the one unmonitored title rather than all three.
        Assert.Equal(1, unmonitored.Facets!.All);
        // The monitoring counts are taken inside the *status* scope, so the
        // monitoring control can say how many fall each side of the state you
        // are looking at. No status here, so it is the whole searched set.
        Assert.Equal(2, unmonitored.Facets.Monitored);
        Assert.Equal(1, unmonitored.Facets.Unmonitored);
    }

    /// <summary>
    /// The point of splitting the axes: a state and an intent can be asked
    /// together. Neither question can be expressed by the other — a missing
    /// title Deluno is hunting for and a missing title you have told it to leave
    /// alone are the same state and opposite intentions.
    /// </summary>
    [Fact]
    public async Task Status_and_monitoring_narrow_movie_rows_together()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var hunted = await movies.AddAsync(new CreateMovieRequest("Hunted", 2019, null), CancellationToken.None);
        var ignored = await movies.AddAsync(new CreateMovieRequest("Ignored", 2020, null), CancellationToken.None);
        var held = await movies.AddAsync(new CreateMovieRequest("Held", 2021, null), CancellationToken.None);

        await movies.EnsureWantedStateAsync(hunted.Id, "library-films", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.EnsureWantedStateAsync(ignored.Id, "library-films", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.EnsureWantedStateAsync(held.Id, "library-films", "covered", "At target.", true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);
        await movies.UpdateMonitoredAsync([ignored.Id], monitored: false, CancellationToken.None);

        var huntedNow = await movies.ListPageAsync(
            new CatalogueQuery(Status: CatalogueStatusFilters.Missing, Monitored: true),
            CancellationToken.None);
        Assert.Equal(hunted.Id, Assert.Single(huntedNow.Items).Id);
        Assert.Equal(1, huntedNow.TotalCount);

        var missingAndIgnored = await movies.ListPageAsync(
            new CatalogueQuery(Status: CatalogueStatusFilters.Missing, Monitored: false),
            CancellationToken.None);
        Assert.Equal(ignored.Id, Assert.Single(missingAndIgnored.Items).Id);

        // And "either" is still the default: both missing titles come back.
        var eitherWay = await movies.ListPageAsync(
            new CatalogueQuery(Status: CatalogueStatusFilters.Missing),
            CancellationToken.None);
        Assert.Equal(2, eitherWay.TotalCount);
        // Within Missing, one of each — which is what the monitoring control shows.
        Assert.Equal(1, eitherWay.Facets!.Monitored);
        Assert.Equal(1, eitherWay.Facets.Unmonitored);
    }

    /// <summary>
    /// #934: the number on a chip and the number of titles it shows have to
    /// agree, for every chip, once loaded — not just the one you happened to
    /// click through in a manual pass.
    ///
    /// One query with no status narrows nothing, so its facets are the whole
    /// searched set's per-state counts — the same numbers every chip in the
    /// row prints regardless of which one is selected. This asserts each of
    /// those printed numbers against a *separate* query actually filtered to
    /// that status, comparing against the rows that query actually returned
    /// (<c>Items.Count</c>) — not its own <c>TotalCount</c>, which is
    /// <c>facets.TotalFor(status)</c> read straight back by construction and so
    /// would agree even if the WHERE clause filtered nothing at all. That is
    /// exactly the shape the Airing chip's bug took — see
    /// <see cref="Facet_totals_match_the_filtered_page_for_every_status_series"/>.
    /// </summary>
    [Fact]
    public async Task Facet_totals_match_the_filtered_page_for_every_status_movies()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        var missing = await movies.AddAsync(new CreateMovieRequest("Missing Reel", 2019, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(missing.Id, "library-films", WantedStatuses.Missing, "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);

        var upgrade = await movies.AddAsync(new CreateMovieRequest("Upgradable Reel", 2020, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(upgrade.Id, "library-films", WantedStatuses.Upgrade, "Could be better.", true, "WEB 1080p", "Remux 2160p", false, CancellationToken.None);

        var covered = await movies.AddAsync(new CreateMovieRequest("Covered Reel", 2021, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(covered.Id, "library-films", WantedStatuses.Covered, "At target.", true, "Remux 2160p", "Remux 2160p", true, CancellationToken.None);

        var upcoming = await movies.AddAsync(new CreateMovieRequest("Upcoming Reel", 2030, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(upcoming.Id, "library-films", WantedStatuses.Upcoming, "Not out yet.", false, null, "WEB 1080p", false, CancellationToken.None);

        var downloading = await movies.AddAsync(new CreateMovieRequest("Downloading Reel", 2022, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(downloading.Id, "library-films", WantedStatuses.Downloading, "On its way.", false, null, "WEB 1080p", false, CancellationToken.None);

        var underAll = await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None);
        var facets = underAll.Facets!;

        foreach (var status in new[]
        {
            CatalogueStatusFilters.Missing,
            CatalogueStatusFilters.Upgrades,
            CatalogueStatusFilters.Covered,
            CatalogueStatusFilters.Upcoming,
            CatalogueStatusFilters.Downloading
        })
        {
            var underStatus = await movies.ListPageAsync(new CatalogueQuery(Status: status, PageSize: 100), CancellationToken.None);
            Assert.True(
                facets.TotalFor(status) == underStatus.Items.Count,
                $"Movies: the '{status}' chip said {facets.TotalFor(status)} but filtering to it returned {underStatus.Items.Count} rows.");
        }
    }

    /// <summary>
    /// The other axis, the same guarantee: the Unmonitored badge is the count
    /// of unmonitored titles *within the currently selected chip*, not the
    /// whole library's unmonitored total — so turning the toggle on can never
    /// show fewer titles than the number it just promised (#934).
    /// </summary>
    [Fact]
    public async Task Unmonitored_facet_matches_the_filtered_page_under_every_status()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        var missingUnmonitored = await movies.AddAsync(new CreateMovieRequest("Missing And Ignored", 2015, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(missingUnmonitored.Id, "library-films", WantedStatuses.Missing, "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.UpdateMonitoredAsync([missingUnmonitored.Id], monitored: false, CancellationToken.None);

        var upcomingMonitored = await movies.AddAsync(new CreateMovieRequest("Not Yet Released", 2030, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(upcomingMonitored.Id, "library-films", WantedStatuses.Upcoming, "Not out yet.", false, null, "WEB 1080p", false, CancellationToken.None);

        foreach (var status in new[] { CatalogueStatusFilters.All, CatalogueStatusFilters.Missing, CatalogueStatusFilters.Upcoming })
        {
            var underStatus = await movies.ListPageAsync(new CatalogueQuery(Status: status), CancellationToken.None);
            var unmonitoredFacet = underStatus.Facets!.Unmonitored;

            // Against the rows the grid actually draws once the toggle is on
            // (`Items.Count`), not `TotalCount` — which is `facets.TotalFor`
            // read straight back and so cannot expose a WHERE clause that
            // silently disagrees with what the facet counted.
            var underStatusAndUnmonitored = await movies.ListPageAsync(
                new CatalogueQuery(Status: status, Monitored: false, PageSize: 100),
                CancellationToken.None);

            Assert.True(
                unmonitoredFacet == underStatusAndUnmonitored.Items.Count,
                $"Under '{status}', the Unmonitored badge said {unmonitoredFacet} but turning it on returned {underStatusAndUnmonitored.Items.Count} rows.");
        }
    }

    [Fact]
    public async Task Library_filter_narrows_movie_rows_and_facets_in_sql()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var anime = await movies.AddAsync(new CreateMovieRequest("Anime Feature", 2020, null), CancellationToken.None);
        var general = await movies.AddAsync(new CreateMovieRequest("General Feature", 2021, null), CancellationToken.None);

        await movies.EnsureWantedStateAsync(anime.Id, "library-anime", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.EnsureWantedStateAsync(general.Id, "library-general", "missing", "Needs a file.", true, "WEB 720p", "WEB 1080p", false, CancellationToken.None);

        var page = await movies.ListPageAsync(
            new CatalogueQuery(LibraryId: "library-anime", PageSize: 10),
            CancellationToken.None);

        Assert.Equal(anime.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Facets!.All);
        Assert.Equal(0, page.Facets.Downloaded);
        Assert.Equal(1, page.Facets.Missing);
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
        await SeedAsync(movies, 501);

        var page = await movies.ListPageAsync(new CatalogueQuery(PageSize: 100_000), CancellationToken.None);

        // "Give me everything" is the request this whole change exists to stop
        // being possible.
        Assert.Equal(500, page.Items.Count);
        Assert.Equal(501, page.TotalCount);
        Assert.True(page.HasMore);
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

    [Fact]
    public async Task Library_filter_narrows_series_rows_and_facets_in_sql()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        var anime = await series.AddAsync(new CreateSeriesRequest("Anime Series", 2020, null), CancellationToken.None);
        var general = await series.AddAsync(new CreateSeriesRequest("General Series", 2021, null), CancellationToken.None);

        await series.EnsureWantedStateAsync(anime.Id, "library-anime", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        // "Downloaded" on a show means every aired episode is here, which is the
        // rung — not `has_file`, which is true of a show holding one episode out
        // of eighty-seven. Sonarr reads it the same way ("All episodes
        // downloaded"). Seeded as covered so this test stays about the thing it
        // is named for: whether the library filter narrows rows and facets.
        await series.EnsureWantedStateAsync(general.Id, "library-general", "covered", "All aired episodes are here.", true, "WEB 720p", "WEB 1080p", true, CancellationToken.None);

        var page = await series.ListPageAsync(
            new CatalogueQuery(LibraryId: "library-general", PageSize: 10),
            CancellationToken.None);

        Assert.Equal(general.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Facets!.Downloaded);
        Assert.Equal(0, page.Facets.Missing);
    }

    /// <summary>
    /// The series counterpart of
    /// <see cref="Facet_totals_match_the_filtered_page_for_every_status_movies"/>,
    /// and the one that would have caught #934 directly: <c>Airing</c> is a TV-only
    /// chip, and <c>ListPageAsync</c>'s own status filter used to omit the airing
    /// predicate it hands the facets query a few lines below — so picking
    /// "Continuing" filtered nothing while the chip beside it, computed with the
    /// predicate actually present, correctly said zero. Same query, same status,
    /// two different answers.
    /// </summary>
    [Fact]
    public async Task Facet_totals_match_the_filtered_page_for_every_status_series()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);

        var missing = await series.AddAsync(new CreateSeriesRequest("Missing Show", 2019, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(missing.Id, "library-shows", WantedStatuses.Missing, "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);

        var upgrade = await series.AddAsync(new CreateSeriesRequest("Upgradable Show", 2020, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(upgrade.Id, "library-shows", WantedStatuses.Upgrade, "Could be better.", true, "WEB 1080p", "Remux 2160p", false, CancellationToken.None);

        var covered = await series.AddAsync(new CreateSeriesRequest("Covered Show", 2021, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(covered.Id, "library-shows", WantedStatuses.Covered, "At target.", true, "Remux 2160p", "Remux 2160p", true, CancellationToken.None);

        var upcoming = await series.AddAsync(new CreateSeriesRequest("Upcoming Show", 2030, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(upcoming.Id, "library-shows", WantedStatuses.Upcoming, "Not out yet.", false, null, "WEB 1080p", false, CancellationToken.None);

        var downloading = await series.AddAsync(new CreateSeriesRequest("Downloading Show", 2022, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(downloading.Id, "library-shows", WantedStatuses.Downloading, "On its way.", false, null, "WEB 1080p", false, CancellationToken.None);

        var airing = await series.AddAsync(new CreateSeriesRequest("Continuing Show", 2018, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(airing.Id, "library-shows", WantedStatuses.Airing, "More is coming.", true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);

        var underAll = await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None);
        var facets = underAll.Facets!;

        foreach (var status in new[]
        {
            CatalogueStatusFilters.Missing,
            CatalogueStatusFilters.Upgrades,
            CatalogueStatusFilters.Covered,
            CatalogueStatusFilters.Upcoming,
            CatalogueStatusFilters.Downloading,
            CatalogueStatusFilters.Airing
        })
        {
            var underStatus = await series.ListPageAsync(new CatalogueQuery(Status: status, PageSize: 100), CancellationToken.None);
            Assert.True(
                facets.TotalFor(status) == underStatus.Items.Count,
                $"Series: the '{status}' chip said {facets.TotalFor(status)} but filtering to it returned {underStatus.Items.Count} rows.");
        }
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
