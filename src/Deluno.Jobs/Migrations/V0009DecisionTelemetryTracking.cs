using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0009DecisionTelemetryTracking : SqliteSqlMigration
{
    public override int Version => 9;

    public override string Name => "decision_telemetry_tracking";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN decision_quality TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_score INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_meets_cutoff INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_status TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_quality_delta INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_custom_format_score INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_seeder_score INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_size_score INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_release_group TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_estimated_bitrate_mbps REAL NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_size_bytes INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_seeders INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_policy_version TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_matched_custom_formats_json TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_reasons_json TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_risk_flags_json TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN decision_override_used INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_dispatches ADD COLUMN decision_override_reason TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_decision_status
            ON download_dispatches (decision_status, created_utc DESC)
            WHERE decision_status IS NOT NULL;
        """;
}
