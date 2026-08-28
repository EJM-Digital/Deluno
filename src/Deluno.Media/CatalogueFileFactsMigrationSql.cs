using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// The picked file's own facts, cached on the title's row, so #307's filters
/// are index walks rather than scans.
///
/// <para><b>The problem, in one line of SQL.</b> The catalogue page reaches the
/// wanted state through a correlated pick —
/// <c>ws.rowid = (SELECT … ORDER BY … LIMIT 1)</c> — and SQLite cannot index
/// that. A <c>WHERE ws.video_codec = 'HEVC'</c> therefore runs the pick for
/// <i>every title in the library</i> before it can discard one. Seven filters
/// already shipped that way: codec, audio codec, audio channels, release group,
/// path, quality and has-file. They are correct, and at twenty thousand titles
/// they are a full scan wearing a seek's clothes.</para>
///
/// <para><b>Why a trigger and not a repository write.</b> Same reason as V0016.
/// Several paths touch wanted state — import, quality recalculation, marking a
/// file missing, the shared media store — and a derived column's danger is the
/// write path that forgets it. A trigger cannot be forgotten by code that does
/// not know it exists.</para>
///
/// <para><b>Why this is generated.</b> V0016 wrote the same pick subquery eight
/// times: once in the backfill and once per column in each of three triggers.
/// Nine columns would be thirty-six copies of a rule that has to stay identical
/// to <c>CatalogueWantedState.Join</c> or the page filters by one file and
/// displays another. So the pick is written <b>once</b>, here, and every use is
/// generated from it.</para>
/// </summary>
public static class CatalogueFileFactsMigrationSql
{
    /// <summary>
    /// The cached column, and the expression on the picked row that fills it.
    /// </summary>
    private sealed record Fact(string Column, string Expression, string IndexKind);

    /// <summary>
    /// <c>text</c> indexes fold case because that is how they are filtered;
    /// <c>plain</c> is for the ones compared as they are stored.
    /// </summary>
    private static readonly Fact[] Facts =
    [
        new("primary_video_codec", "pick.video_codec", "text"),
        new("primary_audio_codec", "pick.audio_codec", "text"),
        new("primary_audio_channels", "pick.audio_channels", "text"),
        new("primary_release_group", "pick.release_group", "text"),
        new("primary_file_path", "pick.file_path", "text"),
        new("primary_current_quality", "pick.current_quality", "text"),
        new("primary_has_file", "pick.has_file", "plain"),
        new("primary_imported_utc", "pick.imported_utc", "plain"),

        // The container, from the path, because nothing stores it separately and
        // "everything still in AVI" is a real question about a library.
        //
        // rtrim(lower(path), 'abcdefghijklmnopqrstuvwxyz0123456789') strips the
        // extension's characters from the right until it hits the dot, and the
        // substr then takes what was stripped. It is ugly and it is the portable
        // way to do it in SQLite without a function; a path with no dot yields
        // the whole string, which is why the CASE guards on one being present.
        new(
            "primary_container",
            """
            CASE
                WHEN instr(pick.file_path, '.') = 0 THEN NULL
                ELSE lower(replace(
                    substr(pick.file_path, length(rtrim(lower(pick.file_path), 'abcdefghijklmnopqrstuvwxyz0123456789')) + 1),
                    '.', ''))
            END
            """,
            "plain")
    ];

    public static string For(string table, string wantedTable, string foreignKey, string indexPrefix)
    {
        var sql = new StringBuilder();

        foreach (var fact in Facts)
        {
            var type = fact.Column == "primary_has_file" ? "INTEGER" : "TEXT";
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN {fact.Column} {type} NULL;");
        }

        sql.AppendLine();

        foreach (var fact in Facts)
        {
            var indexed = fact.IndexKind == "text"
                ? $"lower(COALESCE({fact.Column}, ''))"
                : $"COALESCE({fact.Column}, '')";

            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{fact.Column}_id ON {table} ({indexed}, id);");
        }

        sql.AppendLine();

        // Everything already in the library, so the columns are true the moment
        // the migration finishes rather than the next time a file changes.
        sql.AppendLine(CultureInfo.InvariantCulture, $"UPDATE {table} SET");
        sql.AppendLine(Assignments(wantedTable, foreignKey, $"{table}.id"));
        sql.AppendLine(";");

        foreach (var (suffix, timing, row) in new[]
                 {
                     ("ai", "AFTER INSERT", "NEW"),
                     ("au", "AFTER UPDATE", "NEW"),
                     ("ad", "AFTER DELETE", "OLD")
                 })
        {
            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER IF NOT EXISTS trg_{indexPrefix}_file_facts_{suffix}");
            sql.AppendLine(CultureInfo.InvariantCulture, $"{timing} ON {wantedTable}");
            sql.AppendLine("BEGIN");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    UPDATE {table} SET");
            sql.AppendLine(Assignments(wantedTable, foreignKey, $"{row}.{foreignKey}"));
            sql.AppendLine(CultureInfo.InvariantCulture, $"    WHERE id = {row}.{foreignKey};");
            sql.AppendLine("END;");
        }

        return sql.ToString();
    }

    /// <summary>
    /// One <c>SET</c> line per cached column, each reading the same picked row.
    /// </summary>
    private static string Assignments(string wantedTable, string foreignKey, string idExpression)
        => string.Join(
            "," + Environment.NewLine,
            Facts.Select(fact =>
                $"    {fact.Column} = (SELECT {fact.Expression.ReplaceLineEndings(" ")} {Pick(wantedTable, foreignKey, idExpression)})"));

    /// <summary>
    /// <b>The same pick the page displays, spelled once.</b>
    ///
    /// <para>This order must stay identical to <c>CatalogueWantedState.Join</c>.
    /// If the two ever disagreed, a shelf would filter by one file's codec and
    /// show another's — and nothing about the result would look wrong.
    /// <c>CatalogueSortableFactsTests</c> already asserts the agreement for
    /// V0016's two columns; the same guard covers these.</para>
    /// </summary>
    private static string Pick(string wantedTable, string foreignKey, string idExpression)
        => $"""
            FROM {wantedTable} pick
                     WHERE pick.{foreignKey} = {idExpression}
                     ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                     LIMIT 1
            """;
}
