
namespace Deluno.Media;

/// <summary>
/// What Deluno decided, on the title's own row — the axis with no equivalent
/// anywhere in the arr suite (#309).
///
/// <para><b>Why these move too.</b> Six filters already asked these questions
/// and every one read through <c>ws</c>, the correlated pick SQLite cannot
/// index — the same full scan the file filters were, for the same reason. They
/// were correct and slow, and nothing about the answer said so. Fixing the file
/// half and leaving this half would have been fixing the instance rather than
/// the shape.</para>
///
/// <para><b>And three that could not be asked at all.</b> A title held in two
/// libraries is two copies under two profiles; two rows carrying a file for one
/// title is a duplicate import; and both are invisible today because the page
/// shows one picked row and says nothing about the others. Those are counts
/// rather than facts about the pick, which is why the generator takes an
/// aggregate form as well.</para>
/// </summary>
public static class CatalogueDecisionFacts
{
    public static readonly CatalogueFileFactsMigrationSql.Fact[] All =
    [
        new("primary_wanted_reason", "pick.wanted_reason", "text"),
        new("primary_target_quality", "pick.target_quality", "text"),
        new("primary_quality_cutoff_met", "COALESCE(pick.quality_cutoff_met, 0)", "plain", "INTEGER"),
        new("primary_last_search_utc", "pick.last_search_utc", "plain"),
        new("primary_next_eligible_search_utc", "pick.next_eligible_search_utc", "plain"),
        new("primary_last_search_result", "pick.last_search_result", "text"),

        // The moment reconciliation noticed the file was gone from disk. Not a
        // fact about the copy you hold — a fact about the copy you no longer do.
        new("primary_missing_detected_utc", "pick.missing_detected_utc", "plain"),

        // Two copies, two profiles, one title. The page shows the picked row and
        // is silent about the rest, so this is invisible without asking.
        new(
            "library_count",
            "SELECT COUNT(*) FROM {wanted} c WHERE c.{fk} = {owner}",
            "plain",
            "INTEGER",
            Aggregate: true),

        // Two rows *with a file* for one title, which is a duplicate import
        // rather than a title deliberately held twice.
        new(
            "file_count",
            "SELECT COUNT(*) FROM {wanted} c WHERE c.{fk} = {owner} AND c.has_file = 1",
            "plain",
            "INTEGER",
            Aggregate: true)
    ];

    /// <summary>
    /// The same list with the aggregates bound to one catalogue's tables.
    /// </summary>
    /// <remarks>
    /// The placeholders are filled here rather than in the generator because
    /// only the aggregates have them, and a generator that knew about them would
    /// have to know which facts were aggregates for a second reason.
    /// </remarks>
    public static IReadOnlyList<CatalogueFileFactsMigrationSql.Fact> For(string wantedTable, string foreignKey)
        => [.. All.Select(fact => fact.Aggregate
            ? fact with { Expression = fact.Expression.Replace("{wanted}", wantedTable).Replace("{fk}", foreignKey) }
            : fact)];
}
