using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// Whether the file you keep still matches the rule you set.
///
/// <para><b>The question nothing else asks.</b> A 2160p file sitting at 4 GB
/// was accepted under a profile that says 2160p should be 7–60 GB. Cleanuparr
/// handles stalled, slow and orphaned <i>downloads</i>; nothing in the arr suite
/// audits whether the files you already hold still conform to your own rules.
/// Today the answer is a spreadsheet (#309).</para>
///
/// <para><b>Both ends matter.</b> Under the floor is a bad copy — a 2160p label
/// on a re-encode. Over the ceiling is wasted disk on a remux nobody asked for.
/// So the verdict has three values and not a boolean.</para>
///
/// <para><b>Why a stored verdict rather than an expression.</b> The comparison
/// needs the tier's bounds, which live in <c>quality_ranks</c> — a different
/// table. A filter joining to it could not be a seek, and the bounds change
/// when somebody edits the quality model, which is not a write to any row this
/// would be derived from. So it is cached, maintained by a trigger for the file
/// changing and recomputed by <c>SyncQualityRanksAsync</c> for the rule
/// changing. Both, or it is right about only half the ways it can go stale.</para>
///
/// <para><b>It reads the picked row, not the cached columns beside it.</b> The
/// first version of this read <c>primary_file_size_bytes</c> and
/// <c>primary_current_quality</c>, which V0016 and V0025 maintain from their own
/// triggers on the same table — and every verdict came out NULL, because those
/// triggers had not run yet when this one fired. SQLite does not promise an
/// order and it is not worth depending on one. Reading the pick directly costs
/// a subquery per write, never per page, and cannot be wrong about ordering
/// because there is nothing to order. <c>ConformanceFilterTests</c> is what
/// caught it.</para>
/// </summary>
public static class CatalogueConformanceMigrationSql
{
    /// <summary>
    /// The verdict, from the cached file size and the tier's bounds.
    ///
    /// <para>A zero bound means "no bound" rather than "size zero", which is how
    /// the quality model spells an unset rule — a tier with no ceiling must not
    /// mark every file over it.</para>
    ///
    /// <para>A title with no file has no verdict. It is not conforming and it is
    /// not breaching; it is a different question, and answering <c>'ok'</c> would
    /// quietly count every empty title as compliant.</para>
    /// </summary>
    public static string Verdict(string table, string wantedTable, string foreignKey, string idExpression)
    {
        var size = $"(SELECT pick.file_size_bytes {CatalogueFileFactsMigrationSql.Pick(wantedTable, foreignKey, idExpression)})";
        var tier = $"(SELECT pick.current_quality {CatalogueFileFactsMigrationSql.Pick(wantedTable, foreignKey, idExpression)})";
        var floor = $"(SELECT r.floor_bytes FROM quality_ranks r WHERE r.name = {tier})";
        var ceiling = $"(SELECT r.ceiling_bytes FROM quality_ranks r WHERE r.name = {tier})";

        return $"""
            CASE
                WHEN {size} IS NULL THEN NULL
                WHEN {floor} > 0 AND {size} < {floor} THEN 'under'
                WHEN {ceiling} > 0 AND {size} > {ceiling} THEN 'over'
                -- A tier Deluno has no rule for cannot be judged, and saying
                -- 'ok' would be a verdict rather than the absence of one. That
                -- covers both an unknown tier and a known one whose rule is
                -- unset: "ok" has to mean checked and passed, or the filter is
                -- not worth trusting for an audit.
                WHEN NOT EXISTS (SELECT 1 FROM quality_ranks r WHERE r.name = {tier}) THEN NULL
                WHEN {floor} <= 0 AND {ceiling} <= 0 THEN NULL
                ELSE 'ok'
            END
            """;
    }

    public static string For(string table, string wantedTable, string foreignKey, string indexPrefix)
    {
        var sql = new StringBuilder();

        sql.AppendLine("ALTER TABLE quality_ranks ADD COLUMN floor_bytes INTEGER NOT NULL DEFAULT 0;");
        sql.AppendLine("ALTER TABLE quality_ranks ADD COLUMN ceiling_bytes INTEGER NOT NULL DEFAULT 0;");
        sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN size_conformance TEXT NULL;");
        sql.AppendLine();
        sql.AppendLine(CultureInfo.InvariantCulture,
            $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_size_conformance_id ON {table} (COALESCE(size_conformance, ''), id);");
        sql.AppendLine();

        // Nothing to backfill on a fresh database and nothing to backfill on an
        // existing one either: the shipped ladder is seeded without bounds, so
        // every verdict is NULL until the quality model is next saved. Said out
        // loud because "the filter returns nothing" is otherwise indistinguishable
        // from a defect, and this one resolves itself the first time somebody
        // opens Quality & Release.
        sql.AppendLine(CultureInfo.InvariantCulture, $"UPDATE {table} SET size_conformance = {Verdict(table, wantedTable, foreignKey, $"{table}.id")};");

        foreach (var (suffix, timing, row) in new[]
                 {
                     ("ai", "AFTER INSERT", "NEW"),
                     ("au", "AFTER UPDATE", "NEW"),
                     ("ad", "AFTER DELETE", "OLD")
                 })
        {
            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER IF NOT EXISTS trg_{indexPrefix}_conformance_{suffix}");
            sql.AppendLine(CultureInfo.InvariantCulture, $"{timing} ON {wantedTable}");
            sql.AppendLine("BEGIN");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    UPDATE {table} SET size_conformance = {Verdict(table, wantedTable, foreignKey, $"{row}.{foreignKey}")}");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    WHERE id = {row}.{foreignKey};");
            sql.AppendLine("END;");
        }

        return sql.ToString();
    }
}
