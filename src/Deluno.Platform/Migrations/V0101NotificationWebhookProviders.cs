using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Dedicated notification providers beyond the generic webhook and sniffed
/// Discord embed (#855). Existing rows default to "generic" so nothing about
/// how they already deliver changes.
/// </summary>
public sealed class V0101NotificationWebhookProviders : SqliteSqlMigration
{
    public override int Version => 101;

    public override string Name => "notification_webhook_providers";

    protected override string Sql =>
        """
        ALTER TABLE notification_webhooks ADD COLUMN provider TEXT NOT NULL DEFAULT 'generic';
        ALTER TABLE notification_webhooks ADD COLUMN config_json TEXT NULL;
        """;
}
