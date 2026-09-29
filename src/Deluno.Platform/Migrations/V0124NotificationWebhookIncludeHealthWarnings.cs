using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr and Sonarr's per-connection "Include Health Warnings" (#1016). Every
/// connection that exists today already hears about every health problem, so
/// existing rows are kept on (the column default); a connection created after
/// this starts off and asks for the smaller advisory warnings explicitly
/// (#936).
/// </summary>
public sealed class V0124NotificationWebhookIncludeHealthWarnings : SqliteSqlMigration
{
    public override int Version => 124;

    public override string Name => "notification_webhook_include_health_warnings";

    protected override string Sql =>
        """
        ALTER TABLE notification_webhooks ADD COLUMN include_health_warnings INTEGER NOT NULL DEFAULT 1;
        """;
}
