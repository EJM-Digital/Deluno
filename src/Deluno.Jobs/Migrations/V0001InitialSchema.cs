using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public static class JobsDatabaseMigrations
{
    public static readonly IReadOnlyList<IDelunoDatabaseMigration> All =
    [
        new V0001InitialSchema(),
        new V0002JobIntegrity(),
        new V0003DownloadOutcomeTracking(),
        new V0004ImportResolutions(),
        new V0005DispatchAlerts(),
        new V0006DownloadRetryTracking(),
        new V0007IntegrationCircuitState(),
        new V0008DownloadRetryWindowTracking(),
        new V0009DecisionTelemetryTracking()
    ];

    private sealed class V0001InitialSchema : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "initial_schema";

        protected override string Sql =>
            """
            CREATE TABLE IF NOT EXISTS job_queue (
                id TEXT PRIMARY KEY,
                job_type TEXT NOT NULL,
                source TEXT NOT NULL,
                status TEXT NOT NULL,
                payload_json TEXT NULL,
                attempts INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                scheduled_utc TEXT NOT NULL,
                started_utc TEXT NULL,
                completed_utc TEXT NULL,
                leased_until_utc TEXT NULL,
                worker_id TEXT NULL,
                last_error TEXT NULL,
                related_entity_type TEXT NULL,
                related_entity_id TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_job_queue_status_scheduled
                ON job_queue (status, scheduled_utc);

            CREATE INDEX IF NOT EXISTS ix_job_queue_type_status_scheduled
                ON job_queue (job_type, status, scheduled_utc);

            CREATE TABLE IF NOT EXISTS activity_events (
                id TEXT PRIMARY KEY,
                category TEXT NOT NULL,
                message TEXT NOT NULL,
                details_json TEXT NULL,
                related_job_id TEXT NULL,
                related_entity_type TEXT NULL,
                related_entity_id TEXT NULL,
                created_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_activity_events_created_utc
                ON activity_events (created_utc DESC);

            CREATE TABLE IF NOT EXISTS worker_heartbeats (
                worker_id TEXT PRIMARY KEY,
                last_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_automation_state (
                library_id TEXT PRIMARY KEY,
                library_name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                status TEXT NOT NULL,
                search_requested INTEGER NOT NULL DEFAULT 0,
                last_planned_utc TEXT NULL,
                last_started_utc TEXT NULL,
                last_completed_utc TEXT NULL,
                next_search_utc TEXT NULL,
                last_job_id TEXT NULL,
                last_error TEXT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_library_automation_state_next_search
                ON library_automation_state (next_search_utc);

            CREATE TABLE IF NOT EXISTS search_cycle_runs (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                library_name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                trigger_kind TEXT NOT NULL,
                status TEXT NOT NULL,
                planned_count INTEGER NOT NULL DEFAULT 0,
                queued_count INTEGER NOT NULL DEFAULT 0,
                skipped_count INTEGER NOT NULL DEFAULT 0,
                notes_json TEXT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_search_cycle_runs_library_started
                ON search_cycle_runs (library_id, started_utc DESC);

            CREATE TABLE IF NOT EXISTS search_retry_windows (
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                media_type TEXT NOT NULL,
                action_kind TEXT NOT NULL,
                next_eligible_utc TEXT NOT NULL,
                last_attempt_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 1,
                last_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (entity_type, entity_id, library_id, action_kind)
            );

            CREATE INDEX IF NOT EXISTS ix_search_retry_windows_eligible
                ON search_retry_windows (library_id, media_type, action_kind, next_eligible_utc);

            CREATE TABLE IF NOT EXISTS download_dispatches (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                media_type TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                release_name TEXT NOT NULL,
                indexer_name TEXT NOT NULL,
                download_client_id TEXT NOT NULL,
                download_client_name TEXT NOT NULL,
                status TEXT NOT NULL,
                notes_json TEXT NULL,
                created_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_download_dispatches_media_created
                ON download_dispatches (media_type, created_utc DESC);
            
            """;
    }
}
