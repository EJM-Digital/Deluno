using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// The number above the shelf counts the rows on the shelf — for every status,
/// not just the three somebody remembered.
///
/// <para><b>This is #322 rule 2, and it was quietly broken.</b> Each catalogue
/// had a private <c>SelectFacetTotal</c> naming <c>downloaded</c>, <c>missing</c>
/// and <c>upgrades</c>, with a catch-all sending everything else to the size of
/// the whole library. So a shelf filtered to <i>Quality met</i> printed 11 above
/// a single row, and <i>Upcoming</i> printed 11 above none.</para>
///
/// <para><b>The shape is what makes it dangerous.</b> A catch-all returning a
/// plausible number is invisible until somebody reads the two numbers side by
/// side, and it silently absorbs every status added afterwards. Two had already
/// fallen in before a third was added on top. So this test walks the whole
/// vocabulary rather than the values anybody thought to check — a status added
/// tomorrow is covered by it the day it is added.</para>
/// </summary>
public sealed class CountsAboveTheShelfTests
{
    [Fact]
    public async Task Every_status_the_shelf_offers_counts_the_rows_it_shows()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        // A library with something in every interesting state, so no status is
        // trivially right by being empty on both sides.
        await AddAsync(movies, "Arrival", WantedStatuses.Covered, hasFile: true, cutoffMet: true);
        await AddAsync(movies, "Dune", WantedStatuses.Upgrade, hasFile: true, cutoffMet: false);
        await AddAsync(movies, "Tenet", WantedStatuses.Missing, hasFile: false, cutoffMet: false);
        await AddAsync(movies, "Mickey 17", WantedStatuses.Upcoming, hasFile: false, cutoffMet: false);
        await AddAsync(movies, "Sinners", WantedStatuses.Downloading, hasFile: false, cutoffMet: false);

        foreach (var status in new[]
                 {
                     CatalogueStatusFilters.All,
                     CatalogueStatusFilters.Downloaded,
                     CatalogueStatusFilters.Missing,
                     CatalogueStatusFilters.Upgrades,
                     CatalogueStatusFilters.Covered,
                     CatalogueStatusFilters.Upcoming,
                     CatalogueStatusFilters.Downloading
                 })
        {
            var page = await movies.ListPageAsync(
                new CatalogueQuery(Status: status, PageSize: 50),
                CancellationToken.None);

            Assert.Equal(page.Items.Count, page.TotalCount);
        }
    }

    private static async Task AddAsync(
        SqliteMovieCatalogRepository movies,
        string title,
        string wantedStatus,
        bool hasFile,
        bool cutoffMet)
    {
        await movies.AddAsync(new CreateMovieRequest(title, 2020, null), CancellationToken.None);

        var id = (await movies.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None))
            .Items.Single(item => item.Title == title).Id;

        await movies.EnsureWantedStateAsync(
            id, "library-movies", wantedStatus, "Seeded by a test.",
            hasFile, hasFile ? "WEB 1080p" : null, "WEB 1080p", cutoffMet,
            CancellationToken.None);
    }

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-03-02T00:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
