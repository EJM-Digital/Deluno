using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// Ordering by a rating source walks its index instead of sorting the library.
///
/// <para>#306 sets the bar: "every filter is a WHERE clause on an indexed
/// column", and a sort is the same bargain. A shelf of twenty thousand titles
/// ordered by IMDb score must be a seek down an index, not a scan of the whole
/// catalogue with a temporary B-tree on the end. Nothing about that failure is
/// visible in a test that only checks the rows come back in the right order —
/// they do, eventually.</para>
///
/// <para>It has already happened here once. An index added for the subtitle
/// rollup looked harmless, SQLite preferred it for the catalogue's correlated
/// pick, and one page went from milliseconds to <b>13.4 seconds</b>. Eight new
/// indexes went on each catalogue with this change, so the plan is worth
/// asserting rather than assuming.</para>
/// </summary>
public sealed class RatingSortQueryPlanTests
{
    [Fact]
    public async Task Each_rating_order_is_an_index_walk_rather_than_a_sort_of_the_library()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);

        foreach (var source in RatingSources.All)
        {
            var plan = await ExplainAsync(storage, CatalogueSortFields.ForRating(source.Source));

            // A temporary B-tree for the ordering is the thing that does not
            // scale: it means SQLite read every row before it could return the
            // first one.
            Assert.All(plan, line =>
                Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", line, StringComparison.Ordinal));

            Assert.Contains(plan, line => line.Contains("movie_entries", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// And so is every other order the shelf offers.
    ///
    /// <para>Written as "every order in the served list" rather than a list of
    /// the ones added today, because the failure this catches is an order
    /// declared without the index behind it — which is exactly the mistake
    /// somebody makes when adding the fourteenth one.</para>
    /// </summary>
    [Fact]
    public async Task No_order_the_shelf_offers_sorts_the_whole_library()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);

        foreach (var sort in CatalogueSortFields.ForKind(MediaKind.Movie))
        {
            // Quality and size live on the wanted state and are cached onto the
            // entry by V0016's trigger; bitrate is an expression over two of
            // those. All three are covered by CatalogueSearchStateOnPageTests
            // against the real page query, which is the only place their join
            // exists.
            if (sort is CatalogueSortFields.Size or CatalogueSortFields.Quality or CatalogueSortFields.Bitrate)
            {
                continue;
            }

            var plan = await ExplainAsync(storage, sort);

            Assert.False(
                plan.Any(line => line.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal)),
                $"Ordering by '{sort}' [{CatalogueKeyset.SortExpression(sort, "m", "release_year")}] "
                + $"sorts the whole library: {string.Join(" | ", plan)}");
        }
    }

    /// <summary>
    /// And the default order is untouched by the new indexes.
    ///
    /// <para>This is the half that bit last time: the regression was not in the
    /// new query but in an old one SQLite decided to plan differently once a new
    /// index existed. #306's own rule — "a page asking for nothing runs exactly
    /// the query it ran before this existed" — is what this checks.</para>
    /// </summary>
    [Fact]
    public async Task The_page_that_asks_for_nothing_still_walks_the_added_order()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);

        var plan = await ExplainAsync(storage, CatalogueSortFields.Added);

        Assert.All(plan, line =>
            Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// And the same for the other shelf, which offers most of the same orders
    /// over its own tables.
    ///
    /// <para>This half was missing. Every order was asserted against
    /// <c>movie_entries</c> and nothing planned one against
    /// <c>series_entries</c>, on the reasoning that the two schemas are
    /// generated from one list so what holds for one holds for the other. That
    /// reasoning is how "one rule in two places that cannot check each other"
    /// gets written — the index names differ, the year column differs, and
    /// SQLite plans each database on its own statistics.</para>
    /// </summary>
    [Fact]
    public async Task No_order_the_other_shelf_offers_sorts_the_whole_library()
    {
        using var storage = TestStorage.Create();
        await InitialiseSeriesAsync(storage);

        foreach (var sort in CatalogueSortFields.ForKind(MediaKind.Series))
        {
            // Same three exceptions as the movie shelf, and for the same
            // reason: they live on the wanted state and are only meaningful
            // against the real page query's join.
            if (sort is CatalogueSortFields.Size or CatalogueSortFields.Quality or CatalogueSortFields.Bitrate)
            {
                continue;
            }

            var plan = await ExplainSeriesAsync(storage, sort);

            Assert.False(
                plan.Any(line => line.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal)),
                $"Ordering shows by '{sort}' [{CatalogueKeyset.SortExpression(sort, "s", "start_year")}] "
                + $"sorts the whole library: {string.Join(" | ", plan)}");
        }
    }

    private static async Task InitialiseAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-29T00:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// The ordering clause the repository would build, planned on its own.
    ///
    /// <para>Built from <see cref="CatalogueKeyset"/> rather than written out,
    /// so a change to the expression is a change to what this asserts — the
    /// expression has to match its index character for character to be used at
    /// all, and spelling it twice is how that quietly stops being true.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExplainAsync(TestStorage storage, string sortField)
    {
        var expression = CatalogueKeyset.SortExpression(sortField, "m", "release_year");

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            EXPLAIN QUERY PLAN
            SELECT m.id, {expression} AS sort_value
            FROM movie_entries m
            ORDER BY sort_value DESC, m.id DESC
            LIMIT 51;
            """;

        var lines = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private static async Task InitialiseSeriesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-29T00:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// The same, with the alias and year column the series repository passes.
    /// Written from <see cref="CatalogueKeyset"/> for the same reason as above.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExplainSeriesAsync(TestStorage storage, string sortField)
    {
        var expression = CatalogueKeyset.SortExpression(sortField, "s", "start_year");

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            EXPLAIN QUERY PLAN
            SELECT s.id, {expression} AS sort_value
            FROM series_entries s
            ORDER BY sort_value DESC, s.id DESC
            LIMIT 51;
            """;

        var lines = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }
}
