using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr's own tag-routing rule (#808) extended to notification
/// connections (#990): a notification webhook carrying tags fires only for a
/// title sharing one of them, and an untagged webhook fires for any title —
/// the same rule already applied to download clients and, from this batch,
/// indexers. New installs and existing webhooks alike start untagged, which
/// preserves today's "fires for everything" behaviour exactly (#936).
/// </summary>
public sealed class V0122NotificationWebhookTags : SqliteSqlMigration
{
    public override int Version => 122;

    public override string Name => "notification_webhook_tags";

    protected override string Sql =>
        """
        ALTER TABLE notification_webhooks ADD COLUMN tags TEXT NOT NULL DEFAULT '';
        """;
}
