using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0002JobIntegrity : SqliteSqlMigration
{
    public override int Version => 2;

    public override string Name => "job_integrity";

    protected override string Sql =>
        """
        ALTER TABLE job_queue ADD COLUMN idempotency_key TEXT NULL;
        ALTER TABLE job_queue ADD COLUMN dedupe_key TEXT NULL;
        ALTER TABLE job_queue ADD COLUMN max_attempts INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE job_queue ADD COLUMN last_attempt_utc TEXT NULL;
        ALTER TABLE job_queue ADD COLUMN next_attempt_utc TEXT NULL;

        UPDATE job_queue
        SET
            max_attempts = CASE WHEN max_attempts < 1 THEN 3 ELSE max_attempts END,
            next_attempt_utc = CASE
                WHEN status IN ('queued', 'failed') THEN scheduled_utc
                ELSE next_attempt_utc
            END;

        CREATE INDEX IF NOT EXISTS ix_job_queue_idempotency_key
            ON job_queue (idempotency_key)
            WHERE idempotency_key IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_job_queue_active_dedupe
            ON job_queue (dedupe_key)
            WHERE dedupe_key IS NOT NULL
              AND status IN ('queued', 'running', 'failed');
        """;
}
