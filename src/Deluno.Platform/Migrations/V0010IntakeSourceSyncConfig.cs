using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0010IntakeSourceSyncConfig : SqliteSqlMigration
{
    public override int Version => 10;

    public override string Name => "intake_source_sync_config";

    protected override string Sql =>
        """
        ALTER TABLE intake_sources ADD COLUMN required_genres TEXT NOT NULL DEFAULT '';
        ALTER TABLE intake_sources ADD COLUMN minimum_rating REAL NULL;
        ALTER TABLE intake_sources ADD COLUMN minimum_year INTEGER NULL;
        ALTER TABLE intake_sources ADD COLUMN maximum_age_days INTEGER NULL;
        ALTER TABLE intake_sources ADD COLUMN allowed_certifications TEXT NOT NULL DEFAULT '';
        ALTER TABLE intake_sources ADD COLUMN audience TEXT NOT NULL DEFAULT 'any';
        ALTER TABLE intake_sources ADD COLUMN sync_interval_hours INTEGER NOT NULL DEFAULT 24;
        ALTER TABLE intake_sources ADD COLUMN last_sync_utc TEXT NULL;
        ALTER TABLE intake_sources ADD COLUMN last_sync_status TEXT NOT NULL DEFAULT 'never';
        ALTER TABLE intake_sources ADD COLUMN last_sync_summary TEXT NULL;
        """;
}
