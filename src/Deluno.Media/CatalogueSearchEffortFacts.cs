namespace Deluno.Media;

/// <summary>
/// How hard Deluno has tried, and how little it got — the last of #309's
/// search-state questions.
///
/// <para><b>"Searched forty times with no grab" is a different problem from
/// "never searched".</b> The first is a title nothing indexed carries, or one
/// whose name Deluno cannot match; the second is simply new. Both look like
/// "still missing" on the shelf, and the fix for each is the opposite of the
/// fix for the other — one wants a different search term or an indexer that
/// carries it, the other wants patience.</para>
///
/// <para>Counted rather than derived at query time: the history table grows by
/// a row per search, so counting it per shelf page would mean an aggregate over
/// a table that is bigger than the catalogue.</para>
/// </summary>
public static class CatalogueSearchEffortFacts
{
    /// <param name="historyTable">The catalogue's own search history.</param>
    public static IReadOnlyList<CatalogueFileFactsMigrationSql.Fact> For(string historyTable, string foreignKey)
    =>
    [
        new(
            "search_attempt_count",
            $"SELECT COUNT(*) FROM {historyTable} h WHERE h.{foreignKey} = {{owner}}",
            "plain",
            "INTEGER",
            Aggregate: true),

        // 'matched' is what the pipeline writes when a release was taken.
        // Everything else — rejected, blocked, nothing found — is effort that
        // came back empty.
        new(
            "search_grab_count",
            $"SELECT COUNT(*) FROM {historyTable} h WHERE h.{foreignKey} = {{owner}} AND h.outcome = 'matched'",
            "plain",
            "INTEGER",
            Aggregate: true)
    ];
}
