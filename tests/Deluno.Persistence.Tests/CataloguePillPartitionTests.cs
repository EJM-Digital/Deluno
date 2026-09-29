using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests;

/// <summary>
/// #1048: every title is in exactly one pill, status pills count monitored
/// titles only, Unmonitored is its own pill, All is the sum of the pills, and
/// clicking a pill changes no number and never needs another filter set.
/// </summary>
public sealed class CataloguePillPartitionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static readonly string[] MovieChips =
    [
        CatalogueStatusFilters.Missing,
        CatalogueStatusFilters.Upgrades,
        CatalogueStatusFilters.Covered,
        CatalogueStatusFilters.Upcoming,
        CatalogueStatusFilters.Downloading
    ];

    private static readonly string[] SeriesChips = [.. MovieChips, CatalogueStatusFilters.Airing];

    [Fact]
    public async Task Movies_partition_into_pills_in_one_library_and_across_two()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        async Task<string> Seed(string title, string library, string status, bool hasFile, bool cutoffMet, bool monitored = true)
        {
            var added = await movies.AddAsync(new CreateMovieRequest(title, 2001, null, monitored), CancellationToken.None);
            await movies.EnsureWantedStateAsync(
                added.Id, library, status, "x", hasFile, hasFile ? "WEB 1080p" : null, "WEB 2160p", cutoffMet, CancellationToken.None);
            return added.Id;
        }

        await Seed("Missing", "lib-a", WantedStatuses.Missing, false, false);
        await Seed("Upgrade", "lib-a", WantedStatuses.Upgrade, true, false);
        await Seed("Covered", "lib-a", WantedStatuses.Covered, true, true);
        await Seed("Upcoming", "lib-a", WantedStatuses.Upcoming, false, false);
        await Seed("Downloading", "lib-a", WantedStatuses.Downloading, false, false);
        // The rig's case: unmonitored and missing, and unmonitored with a file.
        await Seed("Unmonitored missing", "lib-a", WantedStatuses.Missing, false, false, monitored: false);
        await Seed("Unmonitored with file", "lib-a", WantedStatuses.Covered, true, true, monitored: false);
        var two = await Seed("Two libraries", "lib-a", WantedStatuses.Covered, true, true);
        await movies.EnsureWantedStateAsync(two, "lib-b", WantedStatuses.Upgrade, "x", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);
        await Seed("B only", "lib-b", WantedStatuses.Missing, false, false);

        await AssertPartitionAsync(
            MovieChips,
            async query =>
            {
                var page = await movies.ListPageAsync(query, CancellationToken.None);
                return (page.Items.Select(item => item.Id).ToList(), page.Facets!, page.TotalCount);
            });
    }

    [Fact]
    public async Task Retention_reads_titles_on_disk_whatever_their_monitoring_while_the_pill_stays_monitored_only()
    {
        // #1069: the Downloaded pill is monitored-only (#1048), but retention asks by files.
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var monitoredId = (await movies.AddAsync(new CreateMovieRequest("Monitored with file", 2001, null, true), CancellationToken.None)).Id;
        var unmonitoredId = (await movies.AddAsync(new CreateMovieRequest("Unmonitored with file", 2002, null, false), CancellationToken.None)).Id;
        var noFileId = (await movies.AddAsync(new CreateMovieRequest("Unmonitored no file", 2003, null, false), CancellationToken.None)).Id;
        foreach (var id in new[] { monitoredId, unmonitoredId })
        {
            await movies.EnsureWantedStateAsync(id, "lib-a", WantedStatuses.Covered, "x", true, "WEB 1080p", "WEB 2160p", true, CancellationToken.None);
        }

        await movies.EnsureWantedStateAsync(noFileId, "lib-a", WantedStatuses.Missing, "x", false, null, "WEB 2160p", false, CancellationToken.None);

        var pill = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded), CancellationToken.None);
        Assert.Equal([monitoredId], pill.Items.Select(item => item.Id).ToArray());

        var retention = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, IncludeUnmonitoredInStatus: true), CancellationToken.None);
        Assert.Equal(new[] { monitoredId, unmonitoredId }.Order(), retention.Items.Select(item => item.Id).Order());
    }

    [Fact]
    public async Task Shows_partition_into_pills_in_one_library_and_across_two()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        async Task<string> Seed(string title, string library, string status, bool hasFile, bool cutoffMet, bool monitored = true)
        {
            await series.AddAsync(new CreateSeriesRequest(title, 2020, null, monitored), CancellationToken.None);
            var id = (await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None))
                .Items.Single(item => item.Title == title).Id;
            await series.EnsureWantedStateAsync(
                id, library, status, "x", hasFile, hasFile ? "WEB 1080p" : null, "WEB 2160p", cutoffMet, CancellationToken.None);
            return id;
        }

        await Seed("Missing", "lib-a", WantedStatuses.Missing, false, false);
        await Seed("Upgrade", "lib-a", WantedStatuses.Upgrade, true, false);
        await Seed("Covered", "lib-a", WantedStatuses.Covered, true, true);
        await Seed("Upcoming", "lib-a", WantedStatuses.Upcoming, false, false);
        await Seed("Downloading", "lib-a", WantedStatuses.Downloading, false, false);
        await Seed("Airing", "lib-a", WantedStatuses.Airing, true, false);
        await Seed("Unmonitored missing", "lib-a", WantedStatuses.Missing, false, false, monitored: false);
        await Seed("Unmonitored with file", "lib-a", WantedStatuses.Covered, true, true, monitored: false);
        var two = await Seed("Two libraries", "lib-a", WantedStatuses.Covered, true, true);
        await series.EnsureWantedStateAsync(two, "lib-b", WantedStatuses.Upgrade, "x", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);
        await Seed("B only", "lib-b", WantedStatuses.Missing, false, false);

        await AssertPartitionAsync(
            SeriesChips,
            async query =>
            {
                var page = await series.ListPageAsync(query, CancellationToken.None);
                return (page.Items.Select(item => item.Id).ToList(), page.Facets!, page.TotalCount);
            });
    }

    private static async Task AssertPartitionAsync(
        string[] chips,
        Func<CatalogueQuery, Task<(List<string> Ids, CatalogueFacets Facets, int? Total)>> list)
    {
        static int Sum(CatalogueFacets facets)
            => facets.Missing + facets.Upgrades + facets.Covered + facets.Upcoming + facets.Downloading + facets.Airing + facets.Unmonitored;

        static int Count(CatalogueFacets facets, string chip) => chip switch
        {
            CatalogueStatusFilters.Missing => facets.Missing,
            CatalogueStatusFilters.Upgrades => facets.Upgrades,
            CatalogueStatusFilters.Covered => facets.Covered,
            CatalogueStatusFilters.Upcoming => facets.Upcoming,
            CatalogueStatusFilters.Downloading => facets.Downloading,
            CatalogueStatusFilters.Airing => facets.Airing,
            _ => throw new ArgumentOutOfRangeException(nameof(chip))
        };

        foreach (var libraryId in new string?[] { null, "lib-a", "lib-b" })
        {
            var (everyone, baseline, _) = await list(new CatalogueQuery(LibraryId: libraryId, PageSize: 100));
            Assert.Equal(baseline.All, everyone.Count);
            Assert.Equal(baseline.All, Sum(baseline));

            var seen = new HashSet<string>();

            foreach (var chip in chips)
            {
                var (ids, facets, total) = await list(new CatalogueQuery(Status: chip, LibraryId: libraryId, PageSize: 100));

                // Clicking a pill moves no number.
                Assert.Equal(baseline, facets);
                // The list is exactly the pill's number, with no other filter set.
                Assert.Equal(Count(baseline, chip), ids.Count);
                Assert.Equal(ids.Count, total);
                foreach (var id in ids)
                {
                    Assert.True(seen.Add(id), $"{id} is in more than one pill (again under {chip}, library {libraryId})");
                }
            }

            // Unmonitored is its own pill, and it too moves nothing.
            var (unmonitoredIds, unmonitoredFacets, unmonitoredTotal) = await list(
                new CatalogueQuery(Monitored: false, LibraryId: libraryId, PageSize: 100));
            Assert.Equal(baseline, unmonitoredFacets);
            Assert.Equal(baseline.Unmonitored, unmonitoredIds.Count);
            Assert.Equal(unmonitoredIds.Count, unmonitoredTotal);
            foreach (var id in unmonitoredIds)
            {
                Assert.True(seen.Add(id), $"{id} is unmonitored and also under a status pill (library {libraryId})");
            }

            // Together the pills are the whole shelf.
            Assert.Equal(everyone.Count, seen.Count);
        }

        // The unmonitored titles never leak into a status pill.
        var (all, facetsAll, _) = await list(new CatalogueQuery(PageSize: 100));
        Assert.Equal(2, facetsAll.Unmonitored);
        Assert.Equal(all.Count, facetsAll.All);
    }
}
