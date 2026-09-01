using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Durable outbound notification delivery records. A row is created before a
/// request is sent so a process restart leaves an observable, replayable item
/// instead of losing the event in an in-memory retry loop.
/// </summary>
public sealed class V0036NotificationWebhookDeliveries : SqliteSqlMigration
{
    public override int Version => 36;

    public override string Name => "notification_webhook_deliveries";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS notification_webhook_deliveries (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    webhook_id TEXT NOT NULL,\n"
        + "    event_category TEXT NOT NULL,\n"
        + "    title TEXT NOT NULL,\n"
        + "    message TEXT NOT NULL,\n"
        + "    details_json TEXT NULL,\n"
        + "    status TEXT NOT NULL DEFAULT 'pending',\n"
        + "    attempt_count INTEGER NOT NULL DEFAULT 0,\n"
        + "    max_attempts INTEGER NOT NULL DEFAULT 3,\n"
        + "    next_attempt_utc TEXT NULL,\n"
        + "    last_attempt_utc TEXT NULL,\n"
        + "    last_status_code INTEGER NULL,\n"
        + "    last_error TEXT NULL,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_notification_webhook_deliveries_status_next\n"
        + "    ON notification_webhook_deliveries (status, next_attempt_utc, created_utc);\n"
        + "CREATE INDEX IF NOT EXISTS ix_notification_webhook_deliveries_webhook_created\n"
        + "    ON notification_webhook_deliveries (webhook_id, created_utc DESC);";
}
