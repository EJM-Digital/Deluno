using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Retains the typed failure behind the latest outbound notification delivery
/// attempt. The legacy last_error column remains the short compatibility
/// message used by older consumers.
/// </summary>
public sealed class V0046NotificationWebhookFailureDetails : SqliteSqlMigration
{
    public override int Version => 46;

    public override string Name => "notification_webhook_failure_details";

    protected override string Sql =>
        "ALTER TABLE notification_webhook_deliveries ADD COLUMN failure_json TEXT NULL;";
}
