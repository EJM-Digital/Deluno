using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// Which subtitles a title holds, on the title's own row, so a shelf can be
/// asked about them.
///
/// <para><b>Why it cannot be read live.</b> The subtitle rollup runs
/// <i>after</i> a page is fetched, over the hundred rows on it, which is the
/// right shape for drawing a bar and useless for filtering: you cannot narrow a
/// library to "everything missing English subtitles" by looking at the page you
/// already chose. #307 lists this as the last axis Radarr states it cannot
/// do — it filters properties of a movie, never of the file you hold.</para>
///
/// <para><b>Three columns, not one.</b> "Has English" and "has English that is
/// not forced" are different questions, and a library where the only English
/// track is forced signage is exactly the case somebody is hunting for. Holding
/// the full list and the non-forced list separately makes that two conditions
/// on an existing operator rather than a new bespoke filter.</para>
///
/// <para><b>Comma-wrapped, like genres.</b> Stored as <c>,en,fr,</c> so a
/// <c>contains</c> of <c>,en,</c> cannot match the <c>en</c> inside
/// <c>,eng,</c>. That is the bug the genre column already had to solve, and it
/// is why the wrapping is part of the stored value rather than something each
/// query has to remember to add.</para>
/// </summary>
public static class CatalogueSubtitleFactsMigrationSql
{
    private sealed record Fact(string Column, string Where);

    private static readonly Fact[] Facts =
    [
        new("subtitle_languages", "1 = 1"),

        // What you can actually watch the film with. A forced track carries the
        // signage and the foreign dialogue and nothing else.
        new("subtitle_languages_full", "sub.forced = 0"),

        // Sidecar files against tracks inside the container, which is the
        // question behind "what did Deluno actually fetch".
        new("subtitle_sources", "1 = 1")
    ];

    /// <param name="table">The catalogue's entries table.</param>
    /// <param name="stateTable">The per-language subtitle state table.</param>
    /// <param name="ownerColumn">Its foreign key — a movie id, or an episode id.</param>
    /// <param name="ownerToEntry">
    /// How that key reaches an entry id. For a film it is the same column; for a
    /// show it is a hop through <c>episode_entries</c>, because subtitles are
    /// held per episode and the shelf asks about the series.
    /// </param>
    public static string For(string table, string indexPrefix, string stateTable, string ownerColumn, string ownerToEntry)
    {
        var sql = new StringBuilder();

        foreach (var fact in Facts)
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {table} ADD COLUMN {fact.Column} TEXT NULL;");
        }

        sql.AppendLine();

        foreach (var fact in Facts)
        {
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_{fact.Column}_id ON {table} (lower(COALESCE({fact.Column}, '')), id);");
        }

        sql.AppendLine();
        sql.AppendLine(CultureInfo.InvariantCulture, $"UPDATE {table} SET");
        sql.AppendLine(Assignments(stateTable, ownerColumn, ownerToEntry, $"{table}.id"));
        sql.AppendLine(";");

        foreach (var (suffix, timing, row) in new[]
                 {
                     ("ai", "AFTER INSERT", "NEW"),
                     ("au", "AFTER UPDATE", "NEW"),
                     ("ad", "AFTER DELETE", "OLD")
                 })
        {
            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER IF NOT EXISTS trg_{indexPrefix}_subtitle_facts_{suffix}");
            sql.AppendLine(CultureInfo.InvariantCulture, $"{timing} ON {stateTable}");
            sql.AppendLine("BEGIN");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    UPDATE {table} SET");
            sql.AppendLine(Assignments(stateTable, ownerColumn, ownerToEntry, "id"));

            // Narrowed to the one entry the changed row belongs to. Without the
            // WHERE this would rewrite every title in the library on every
            // subtitle write, which a scan does not announce.
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"    WHERE id = ({ownerToEntry.Replace("{owner}", $"{row}.{ownerColumn}")});");
            sql.AppendLine("END;");
        }

        return sql.ToString();
    }

    private static string Assignments(string stateTable, string ownerColumn, string ownerToEntry, string idExpression)
        => string.Join(
            "," + Environment.NewLine,
            Facts.Select(fact =>
            {
                var value = fact.Column == "subtitle_sources" ? "sub.source" : "sub.language";

                return $"""
                        {"    "}{fact.Column} = (
                            SELECT ',' || group_concat(DISTINCT lower({value})) || ','
                            FROM {stateTable} sub
                            WHERE ({ownerToEntry.Replace("{owner}", $"sub.{ownerColumn}")}) = {idExpression}
                              AND {fact.Where})
                        """;
            }));
}
