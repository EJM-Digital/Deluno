using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores the local owner opt-in and latest report for the TRaSH Guides
/// metadata check. It never stores or applies an unreviewed remote package.
/// </summary>
public sealed class V0049GuideUpdateCheckState : SqliteSqlMigration
{
    public override int Version => 49;

    public override string Name => "guide_update_check_state";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS guide_update_check_state (\n"
        + "    id INTEGER PRIMARY KEY CHECK (id = 1),\n"
        + "    is_enabled INTEGER NOT NULL DEFAULT 0,\n"
        + "    last_checked_utc TEXT NULL,\n"
        + "    last_seen_revision TEXT NULL,\n"
        + "    status TEXT NOT NULL,\n"
        + "    error TEXT NULL,\n"
        + "    report_json TEXT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n";
}
