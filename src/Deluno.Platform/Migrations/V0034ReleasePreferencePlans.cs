using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Immutable compiled release-preference plans. Mutable profile rows remain
/// editable; this table is the historical contract referenced by evaluations.
/// </summary>
public sealed class V0034ReleasePreferencePlans : SqliteSqlMigration
{
    public override int Version => 34;

    public override string Name => "release_preference_plans";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS release_preference_plans (\n"
        + "    plan_id TEXT NOT NULL,\n"
        + "    version TEXT NOT NULL,\n"
        + "    media_type TEXT NOT NULL,\n"
        + "    plan_hash TEXT NOT NULL,\n"
        + "    plan_json TEXT NOT NULL,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    PRIMARY KEY (plan_id, version),\n"
        + "    UNIQUE (plan_id, plan_hash)\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_release_preference_plans_media_type\n"
        + "    ON release_preference_plans (media_type, created_utc DESC);";
}
