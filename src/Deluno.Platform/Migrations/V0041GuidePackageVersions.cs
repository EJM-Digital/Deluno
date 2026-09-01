using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Stores owner-approved, immutable guide package versions.</summary>
public sealed class V0041GuidePackageVersions : SqliteSqlMigration
{
    public override int Version => 41;

    public override string Name => "guide_package_versions";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS guide_package_versions (\n"
        + "    package_id TEXT NOT NULL,\n"
        + "    package_version INTEGER NOT NULL,\n"
        + "    integrity_sha256 TEXT NOT NULL,\n"
        + "    package_json TEXT NOT NULL,\n"
        + "    source_revision TEXT NOT NULL,\n"
        + "    is_active INTEGER NOT NULL DEFAULT 0,\n"
        + "    stored_utc TEXT NOT NULL,\n"
        + "    PRIMARY KEY (package_id, package_version)\n"
        + ");\n"
        + "CREATE UNIQUE INDEX IF NOT EXISTS ux_guide_package_versions_active\n"
        + "    ON guide_package_versions (is_active) WHERE is_active = 1;\n";
}
