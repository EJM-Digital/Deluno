using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The indexes that make the TV shelf's own orders seeks rather than sorts of
/// every show you have.
///
/// <para>V0020 indexed <c>next_air_date_utc</c> as a bare column, and the order
/// has always been <c>COALESCE(next_air_date_utc, …)</c> — a show with nothing
/// still to come sorts last rather than first, because "what is on next" is a
/// question about shows that have a next. An expression index only serves an
/// <c>ORDER BY</c> that matches it character for character, and a plain column
/// index does not serve an expression order at all, so every TV shelf ordered
/// by next airing has read the whole table and sorted it in a temp B-tree.</para>
///
/// <para><b>Nothing failed, and nothing was going to.</b> The output is correct;
/// only the cost is wrong. The plan assertions that exist for exactly this
/// ran against <c>movie_entries</c> and there was no series half — so a sort
/// only the TV shelf offers was the one sort nothing planned. The series half
/// exists now, and it caught this on its first run.</para>
///
/// <para>The old bare-column index stays: it still serves the expiry sweep and
/// any filter on the raw date, and dropping it to save a page would trade a
/// known-good plan for a saved byte.</para>
///
/// <para><b>Network is here for the same reason and had no index at all.</b>
/// Both are sorts only the TV shelf offers, which is precisely the set a
/// movie-only plan test cannot see. Two defects, one blind spot.</para>
/// </summary>
public sealed class V0032SeriesTvSortIndexes : SqliteSqlMigration
{
    public override int Version => 32;

    public override string Name => "series_tv_sort_indexes";

    // The sentinel comes from the one place that declares it, because this
    // string and the one in `CatalogueKeyset.SortExpression` have to be
    // identical for any of this to be worth doing. They were two hand-written
    // literals until this migration needed a third.
    protected override string Sql =>
        $"""
        CREATE INDEX IF NOT EXISTS ix_series_entries_next_air_sort
            ON series_entries (COALESCE(next_air_date_utc, '{CatalogueSortFields.Sentinels.NoNextAiring}'), id);

        -- Case-folded, because that is how the order reads it, and because a
        -- shelf grouped by network should put "netflix" and "Netflix" together.
        CREATE INDEX IF NOT EXISTS ix_series_entries_network_sort
            ON series_entries (lower(COALESCE(network, '')), id);
        """;
}
