using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0008NotificationWebhooks : SqliteSqlMigration
{
    public override int Version => 8;

    public override string Name => "notification_webhooks";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS notification_webhooks (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            url TEXT NOT NULL,
            event_filters TEXT NOT NULL DEFAULT '',
            is_enabled INTEGER NOT NULL DEFAULT 1,
            last_fired_utc TEXT NULL,
            last_error TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;
}
