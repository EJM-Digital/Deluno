using System.Text;
using System.Globalization;

namespace Deluno.Media;

/// <summary>
/// What a title is about, beyond its genre.
///
/// <para><b>Its own migration, and not an edit to the one beside it.</b> The
/// facts migration had already run — on the rig, and on anything built from
/// main — and a migration that has been recorded never runs again. Adding the
/// column to that body would have compiled, passed on a fresh database, and
/// left every existing install without it. That failure has no error and no
/// symptom except a filter that quietly matches nothing.</para>
///
/// <para><b>One column, comma separated, like genres.</b> Nothing joins on a
/// keyword — the question is "is this one of them", which the existing
/// <c>contains</c> operator already answers over exactly this shape. A join
/// table would be a second way to store a list of words about a title.</para>
/// </summary>
public static class CatalogueKeywordsMigrationSql
{
    public static string For(string table, string indexPrefix)
    {
        var sql = new StringBuilder();

        sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN keywords TEXT NULL;");
        sql.AppendLine(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_keywords_id ON {table} (lower(COALESCE(keywords, '')), id);");

        // From the blob, for anything already refreshed since the gateway
        // learnt to ask TMDb for them. A row older than that has no such key
        // and stays NULL until its next metadata refresh.
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            UPDATE {table}
            SET keywords = (
                SELECT group_concat(json_extract(entry.value, '$'), ', ')
                FROM json_each(json_extract(metadata_json, '$.Keywords')) AS entry)
            WHERE metadata_json IS NOT NULL
              AND json_valid(metadata_json)
              AND json_type(metadata_json, '$.Keywords') = 'array';
            """);

        return sql.ToString();
    }

    /// <summary>
    /// Before the (shipped, unchangeable) backfill: set aside the stored metadata of every row that holds a
    /// keyword list and blank it in place, so the backfill's <c>metadata_json IS NOT NULL</c> test skips it.
    /// The backfill reads each keyword with <c>json_extract(entry.value, '$')</c>, and for a string element
    /// <c>json_each</c> already hands back the bare text, which is not JSON: SQLite stops with "malformed JSON"
    /// and the database never opens (#1305). Nothing is lost: <see cref="RepairAfter"/> puts the text back
    /// verbatim in the same transaction.
    /// </summary>
    public static string RepairBefore(string table) => $"""
        CREATE TEMP TABLE keywords_repair AS
        SELECT id, metadata_json FROM {table}
        WHERE metadata_json IS NOT NULL
          AND json_valid(metadata_json)
          AND json_type(metadata_json, '$.Keywords') = 'array';
        UPDATE {table} SET metadata_json = NULL WHERE id IN (SELECT id FROM keywords_repair);
        """;

    /// <summary>
    /// After the backfill: restore the stored metadata byte for byte and list its keywords the right way
    /// (the element text itself, as <c>json_each</c> returns it).
    /// </summary>
    public static string RepairAfter(string table) => $"""
        UPDATE {table}
        SET metadata_json = (SELECT r.metadata_json FROM keywords_repair r WHERE r.id = {table}.id),
            keywords = (
                SELECT group_concat(entry.value, ', ')
                FROM keywords_repair r, json_each(json_extract(r.metadata_json, '$.Keywords')) AS entry
                WHERE r.id = {table}.id)
        WHERE id IN (SELECT id FROM keywords_repair);
        DROP TABLE keywords_repair;
        """;
}
