using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// The two pieces of an indexer's own history (#754) that no existing table
/// already records per-indexer: an RSS-sync attempt, and an auth failure.
/// Grabs and searches are read live from the existing per-movie/per-series
/// search history tables, which already carry an indexer name -- this table
/// only adds what those cannot.
///
/// <para><c>indexer_history_clear_state</c> is a single cursor rather than a
/// delete: the combined screen's Clear action must never remove a row that
/// the per-movie or per-series history screens still show, so clearing hides
/// everything at or before the cursor instead of deleting anyone's data.</para>
/// </summary>
public sealed class V0112IndexerHistoryEvents : SqliteSqlMigration
{
    public override int Version => 112;

    public override string Name => "indexer_history_events";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS indexer_history_events (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    indexer_id TEXT NULL,\n"
        + "    indexer_name TEXT NOT NULL,\n"
        + "    event_type TEXT NOT NULL,\n"
        + "    outcome TEXT NOT NULL,\n"
        + "    message TEXT NOT NULL,\n"
        + "    details_json TEXT NULL,\n"
        + "    created_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_indexer_history_events_created_utc ON indexer_history_events (created_utc);\n"
        + "CREATE TABLE IF NOT EXISTS indexer_history_clear_state (\n"
        + "    id INTEGER PRIMARY KEY CHECK (id = 1),\n"
        + "    cleared_before_utc TEXT NOT NULL\n"
        + ");\n";
}
