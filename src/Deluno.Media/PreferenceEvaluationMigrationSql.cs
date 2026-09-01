using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// Schema shared by the movie and TV preference-evaluation history tables.
/// Keeping prior plan hashes is intentional: a plan change must trigger a
/// re-evaluation, not erase the evidence that explained the previous outcome.
/// </summary>
public static class PreferenceEvaluationMigrationSql
{
    public static string For(
        string table,
        string entryTable,
        string mediaIdColumn,
        string indexPrefix)
    {
        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE TABLE IF NOT EXISTS {table} (
                id TEXT PRIMARY KEY,
                media_id TEXT NOT NULL,
                library_id TEXT NULL,
                file_identity TEXT NOT NULL,
                file_path TEXT NULL,
                file_size_bytes INTEGER NULL,
                plan_id TEXT NOT NULL,
                plan_version TEXT NOT NULL,
                plan_hash TEXT NOT NULL,
                facts_json TEXT NOT NULL,
                evaluation_json TEXT NOT NULL,
                matched_rule_ids_json TEXT NOT NULL,
                evaluated_utc TEXT NOT NULL,
                source TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE (media_id, library_id, file_identity, plan_hash),
                FOREIGN KEY (media_id) REFERENCES {entryTable}(id) ON DELETE CASCADE
            );
            """);
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_current
                ON {table} (media_id, library_id, evaluated_utc DESC);
            """);
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_plan
                ON {table} (plan_hash, media_id, library_id);
            """);
        return sql.ToString();
    }
}
