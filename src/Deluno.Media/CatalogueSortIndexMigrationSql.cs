using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// Indexes for the orders #310 counted and Deluno could not perform.
///
/// <para><b>An order without an index is not a slower order, it is a different
/// one.</b> SQLite answers it by reading every row and sorting the lot before
/// returning the first page, so page four hundred of a twenty-thousand-title
/// library costs what the whole library costs. The sort still looks right,
/// which is why this is worth a migration rather than a line in the switch.</para>
///
/// <para>Each index is spelled the way <c>CatalogueKeyset.SortExpression</c>
/// spells it, because SQLite uses an expression index only for an
/// <c>ORDER BY</c> that matches it character for character. Getting that wrong
/// is silent: the sort works, the index is simply never used. It has already
/// cost this codebase a page that went from milliseconds to 13.4 seconds.</para>
/// </summary>
public static class CatalogueSortIndexMigrationSql
{
    /// <summary>
    /// Orders both shelves can be put in.
    ///
    /// <para>The date columns are movie-only — a show has an air date, not a
    /// cinema release — so they are passed in rather than assumed.</para>
    /// </summary>
    public static string For(string table, string indexPrefix, IReadOnlyList<string> dateColumns)
    {
        var sql = new StringBuilder();

        // Monitored is a flag, so this index exists to make it the *leading*
        // column of an order, not to narrow anything: "show me everything I am
        // not watching for, then everything I am".
        sql.AppendLine(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_monitored_id ON {table} (monitored, id);");

        // Folded, because it is ordered the way it is filtered. An index on the
        // raw column would sort "iron man" after "Zulu".
        sql.AppendLine(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_original_title_id ON {table} (lower(COALESCE(original_title, '')), id);");

        foreach (var column in dateColumns)
        {
            // Nulls last in both directions, which is what the COALESCE in the
            // sort expression is for: a library where most films have no
            // physical release should not open on a page of blanks.
            //
            // digital_release_date is here even though V0007 already indexed
            // it, because V0007 indexed the bare column and this orders by the
            // COALESCE. The old one still serves the date *filter*; it cannot
            // serve this order, and the difference is invisible until somebody
            // measures it.
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{column}_sort ON {table} (COALESCE({column}, '9999-12-31'), id);");
        }

        // The title order, which V0021/V0022 indexed as (sort_title, id) while
        // the sort expression is COALESCE(sort_title, lower(title)). A plain
        // column index does not serve an expression ORDER BY, so ordering a
        // library by title has been a full scan with a temp B-tree since #325
        // shipped — correct output, and quietly the most expensive page in the
        // app on the most common order there is.
        //
        // The COALESCE stays: sort_title is filled by a trigger and a backfill,
        // but a row that predates both would otherwise sort under the empty
        // string. So the index is spelled to match instead.
        sql.AppendLine(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_sort_title_sort ON {table} (COALESCE(sort_title, lower(title)), id);");

        return sql.ToString();
    }
}
