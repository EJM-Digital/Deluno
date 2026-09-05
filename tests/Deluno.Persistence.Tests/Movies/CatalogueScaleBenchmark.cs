using System.Diagnostics;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// The catalogue at the size the design notes keep invoking.
///
/// "Twenty thousand titles" appears throughout as the number the paged query
/// exists for, and the existing coverage walks two thousand. James asked what
/// actually happens at ten and twenty. This measures it rather than repeating
/// the claim.
///
/// Two very different questions live here:
///
///   The **page** is a keyset seek, so page one and page two hundred should cost
///   the same, and neither should grow with the library.
///
///   The **facets** — the counts above the shelf — are a deliberate scan, taken
///   once per filter rather than once per page. That scan is what is most likely
///   to be felt at size, because every row also evaluates correlated EXISTS
///   subqueries for has-file, upgrade, covered and upcoming.
///
/// Read the numbers with:
/// <c>dotnet test --filter CatalogueScaleBenchmark -l "console;verbosity=detailed"</c>
/// </summary>
public sealed class CatalogueScaleBenchmark(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(20_000)]
    public async Task A_catalogue_of_this_size_pages_and_counts_without_the_size_showing(int total)
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        var seeding = Stopwatch.StartNew();
        await SeedAsync(storage, total);
        seeding.Stop();
        output.WriteLine($"seeded {total:N0} titles with wanted state in {seeding.ElapsedMilliseconds:N0} ms");

        var firstPageClock = Stopwatch.StartNew();
        var firstPage = await movies.ListPageAsync(new CatalogueQuery(PageSize: 100), CancellationToken.None);
        firstPageClock.Stop();

        Assert.Equal(100, firstPage.Items.Count);
        Assert.Equal(total, firstPage.TotalCount);
        Assert.NotNull(firstPage.Facets);
        output.WriteLine($"first page + facets   {firstPageClock.ElapsedMilliseconds,6:N0} ms  (facets scan all {total:N0})");

        var token = firstPage.NextPageToken;
        var pages = 1;
        long slowestContinuation = 0;
        var continuationTimings = new List<double>();
        var walk = Stopwatch.StartNew();
        while (token is not null)
        {
            var pageClock = Stopwatch.StartNew();
            var page = await movies.ListPageAsync(
                new CatalogueQuery(PageSize: 100, PageToken: token), CancellationToken.None);
            pageClock.Stop();
            slowestContinuation = Math.Max(slowestContinuation, pageClock.ElapsedMilliseconds);
            continuationTimings.Add(pageClock.Elapsed.TotalMilliseconds);

            // A continuation page must never recompute the counts.
            Assert.Null(page.Facets);
            token = page.NextPageToken;
            pages++;
        }
        walk.Stop();

        output.WriteLine($"walked {pages} pages        {walk.ElapsedMilliseconds,6:N0} ms  ({walk.ElapsedMilliseconds / (double)pages:F1} ms average, {slowestContinuation} ms worst)");

        var filteredClock = Stopwatch.StartNew();
        var filtered = await movies.ListPageAsync(
            new CatalogueQuery(PageSize: 100, Status: CatalogueStatusFilters.Missing), CancellationToken.None);
        filteredClock.Stop();
        output.WriteLine($"filter to Missing     {filteredClock.ElapsedMilliseconds,6:N0} ms  ({filtered.TotalCount:N0} matched)");

        // A leading wildcard cannot use an index, so this is the honest worst
        // case for a library-sized catalogue.
        var searchClock = Stopwatch.StartNew();
        var searched = await movies.ListPageAsync(
            new CatalogueQuery(PageSize: 100, Search: "title 1"), CancellationToken.None);
        searchClock.Stop();
        output.WriteLine($"search 'title 1'      {searchClock.ElapsedMilliseconds,6:N0} ms  ({searched.TotalCount:N0} matched)");

        Assert.Equal(total, pages * 100);

        // The median page, not the worst one.
        //
        // This asserted on the slowest of two hundred pages and failed CI at
        // 278 ms — in a run whose average was 6.9 ms. One page in two hundred
        // hitting a garbage collection or losing its scheduling slice on a
        // shared runner says nothing about the query, and the test was reading
        // it as though it did.
        //
        // A keyset seek that has degenerated into a scan is slow on *every*
        // page, by construction: the cost grows with the offset, so the median
        // moves before the maximum does and moves further. Measuring the median
        // therefore catches the regression this exists for, sooner, and is
        // immune to a single outlier. The bound stays deliberately loose — it
        // guards an order of magnitude, not a target, on hardware of unknown
        // speed.
        continuationTimings.Sort();
        var medianContinuation = continuationTimings[continuationTimings.Count / 2];
        output.WriteLine($"median continuation   {medianContinuation,6:F1} ms  (worst {slowestContinuation} ms, ignored)");

        Assert.True(
            medianContinuation < 50,
            $"The median continuation page took {medianContinuation:F1} ms at {total:N0} titles, which suggests the keyset seek has become a scan.");
    }

    /// <summary>
    /// Straight into SQLite in one transaction. Going through AddAsync and
    /// EnsureWantedStateAsync would be forty thousand round trips and would
    /// measure the seeding rather than the reading.
    /// </summary>
    private static async Task SeedAsync(TestStorage storage, int total)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        const string now = "2026-08-27T00:00:00.0000000+00:00";

        for (var index = 0; index < total; index++)
        {
            var id = $"movie-{index:D6}";

            using (var entry = connection.CreateCommand())
            {
                entry.Transaction = transaction;
                entry.CommandText =
                    "INSERT INTO movie_entries (id, title, release_year, monitored, created_utc, updated_utc) " +
                    "VALUES (@id, @title, @year, 1, @now, @now);";
                AddParameter(entry, "@id", id);
                AddParameter(entry, "@title", $"Title {index:D6}");
                AddParameter(entry, "@year", 1990 + (index % 30));
                AddParameter(entry, "@now", now);
                await entry.ExecuteNonQueryAsync(CancellationToken.None);
            }

            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
                "INSERT INTO movie_wanted_state " +
                "(movie_id, library_id, wanted_status, wanted_reason, has_file, current_quality, target_quality, quality_cutoff_met, updated_utc) " +
                "VALUES (@movieId, 'library-films', @status, 'seeded', @hasFile, @currentQuality, 'WEB 2160p', @cutoffMet, @now);";
            AddParameter(wanted, "@movieId", id);
            // A spread across all four rungs, so every facet arm has rows to
            // count rather than short-circuiting on an empty set.
            var rung = index % 4;
            AddParameter(wanted, "@status", rung switch { 0 => "missing", 1 => "upgrade", 2 => "covered", _ => "upcoming" });
            AddParameter(wanted, "@hasFile", rung is 1 or 2 ? 1 : 0);
            AddParameter(wanted, "@currentQuality", rung is 1 or 2 ? "WEB 1080p" : null);
            AddParameter(wanted, "@cutoffMet", rung == 2 ? 1 : 0);
            AddParameter(wanted, "@now", now);
            await wanted.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
