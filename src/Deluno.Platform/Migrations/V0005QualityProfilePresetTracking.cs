using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0005QualityProfilePresetTracking : SqliteSqlMigration
{
    public override int Version => 5;

    public override string Name => "quality_profile_preset_tracking";

    protected override string Sql =>
        """
        ALTER TABLE quality_profiles ADD COLUMN preset_id TEXT NULL;
        ALTER TABLE quality_profiles ADD COLUMN preset_version INTEGER NULL;

        CREATE INDEX IF NOT EXISTS ix_quality_profiles_preset_id
            ON quality_profiles (preset_id)
            WHERE preset_id IS NOT NULL;
        """;
}
