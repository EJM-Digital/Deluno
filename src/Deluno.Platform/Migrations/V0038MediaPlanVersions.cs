using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Immutable audit history for the current media-plan projection. A mutable
/// policy_sets row remains the active runtime pointer; every create, edit and
/// rollback appends a complete snapshot here so preview and recovery never
/// depend on reconstructing old state from a later row.
/// </summary>
public sealed class V0038MediaPlanVersions : SqliteSqlMigration
{
    public override int Version => 38;

    public override string Name => "media_plan_versions";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS media_plan_versions (\n"
        + "    plan_id TEXT NOT NULL,\n"
        + "    version INTEGER NOT NULL,\n"
        + "    plan_hash TEXT NOT NULL,\n"
        + "    change_kind TEXT NOT NULL,\n"
        + "    snapshot_json TEXT NOT NULL,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    PRIMARY KEY (plan_id, version)\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_media_plan_versions_created\n"
        + "    ON media_plan_versions (plan_id, created_utc DESC, version DESC);\n";
}
