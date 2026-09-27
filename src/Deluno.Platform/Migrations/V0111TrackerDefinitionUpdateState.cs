using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores the local owner opt-in, cadence, and last-run outcome for the
/// scheduled tracker-definition update job (#756). Off by default (#936):
/// absent is read the same as an explicit "false" would be, so a new install
/// never fetches anything until a person turns this on.
/// </summary>
public sealed class V0111TrackerDefinitionUpdateState : SqliteSqlMigration
{
    public override int Version => 111;

    public override string Name => "tracker_definition_update_state";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS tracker_definition_update_state (\n"
        + "    id INTEGER PRIMARY KEY CHECK (id = 1),\n"
        + "    is_enabled INTEGER NOT NULL DEFAULT 0,\n"
        + "    interval_hours INTEGER NOT NULL DEFAULT 24,\n"
        + "    last_checked_utc TEXT NULL,\n"
        + "    last_updated_utc TEXT NULL,\n"
        + "    status TEXT NOT NULL,\n"
        + "    error TEXT NULL,\n"
        + "    updated_ids_json TEXT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n";
}
