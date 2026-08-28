using System.Globalization;
using System.Text;
using Deluno.Contracts;

namespace Deluno.Media;

/// <summary>
/// The columns #306 and #319 need, written once and run against both
/// catalogues.
///
/// <para>Movies and shows take the same six title facts and the same four
/// ratings, and the only thing that differs is the table name. Writing the
/// migration twice is how <c>network</c> ended up with no writer for four
/// versions — so this generates it from <see cref="RatingSources.All"/>, and a
/// source added to that list gets its column, its index and its backfill on
/// both shelves or on neither.</para>
///
/// <para><b>The backfill reads the blob nothing was reading.</b> Certification,
/// collection and original language have been arriving inside
/// <c>metadata_json</c> and going nowhere; the ratings have been in there as a
/// JSON array that only the detail page ever unpacked. A row written before the
/// provider learnt to send a field has no such key and stays NULL until a
/// metadata refresh — the same bargain #326 makes about artwork.</para>
/// </summary>
public static class CatalogueFactsMigrationSql
{
    public static string For(string table, string indexPrefix)
    {
        var sql = new StringBuilder();

        // Three facts the adapters have read out of the blob since long before
        // anything wrote them, so they have returned empty on every install.
        foreach (var column in new[] { "certification", "collection", "original_language" })
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;");
        }

        foreach (var source in RatingSources.All)
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN {source.ScoreColumn} REAL NULL;");
            if (source.VotesColumn is not null)
            {
                sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN {source.VotesColumn} INTEGER NULL;");
            }
        }

        sql.AppendLine();

        // Text facts are filtered case-insensitively and sorted the way they
        // are filtered, so the index has to hold the folded value or the page
        // stops being a seek.
        foreach (var column in new[] { "certification", "collection", "original_language" })
        {
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{column}_id ON {table} (lower(COALESCE({column}, '')), id);");
        }

        // On the COALESCE, not the bare column, and this is the whole
        // difference between a seek and a scan.
        //
        // The sort expression is COALESCE(rating_imdb, -1) so that titles with
        // no score sort last instead of first, and SQLite will only use an
        // expression index for an ORDER BY that matches it *character for
        // character*. A plain index on (rating_imdb, id) does not serve it, and
        // the first version of this migration was partial as well — WHERE score
        // IS NOT NULL — which cannot order the whole table at all. Both looked
        // sensible and both left the page sorting twenty thousand rows before
        // returning the first one. RatingSortQueryPlanTests is what caught it.
        //
        // Spelled from the same place CatalogueKeyset spells it, so the two
        // cannot drift into disagreeing silently.
        foreach (var source in RatingSources.All)
        {
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{source.ScoreColumn}_id ON {table} (COALESCE({source.ScoreColumn}, -1), id);");

            if (source.VotesColumn is not null)
            {
                sql.AppendLine(CultureInfo.InvariantCulture,
                    $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{source.VotesColumn}_id ON {table} ({source.VotesColumn}, id) WHERE {source.VotesColumn} IS NOT NULL;");
            }
        }

        sql.AppendLine();
        sql.AppendLine(CultureInfo.InvariantCulture, $"UPDATE {table}");
        sql.AppendLine("SET certification = json_extract(metadata_json, '$.Certification'),");
        sql.AppendLine("    collection = json_extract(metadata_json, '$.Collection'),");
        sql.AppendLine("    original_language = json_extract(metadata_json, '$.OriginalLanguage'),");

        var assignments = new List<string>();
        foreach (var source in RatingSources.All)
        {
            assignments.Add($"    {source.ScoreColumn} = {RatingFromBlob(source.Source, "Score")}");
            if (source.VotesColumn is not null)
            {
                assignments.Add($"    {source.VotesColumn} = {RatingFromBlob(source.Source, "VoteCount")}");
            }
        }

        sql.AppendLine(string.Join(",\n", assignments));
        sql.AppendLine("WHERE metadata_json IS NOT NULL AND json_valid(metadata_json);");

        return sql.ToString();
    }

    /// <summary>
    /// One score out of the stored <c>Ratings</c> array.
    ///
    /// <para><c>json_each</c> over a column of a row being updated is a
    /// correlated subquery, which is fine here: it runs once per row, over an
    /// array of at most four entries, in a migration.</para>
    /// </summary>
    private static string RatingFromBlob(string source, string field)
        => $"""
            (SELECT json_extract(entry.value, '$.{field}')
             FROM json_each(json_extract(metadata_json, '$.Ratings')) AS entry
             WHERE json_extract(entry.value, '$.Source') = '{source}'
             LIMIT 1)
            """;
}
