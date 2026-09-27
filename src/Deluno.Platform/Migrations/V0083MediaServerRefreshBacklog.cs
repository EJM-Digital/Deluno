using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// One row per media server and media type currently failing its refresh
/// (#920): when it first failed (System → Health's "hasn't answered since"),
/// when to try again, and every file it missed so a successful retry can
/// catch up in one go. Kept per media type because a client's own refresh
/// call is per-library. The row is deleted the moment that library answers
/// again.
/// </summary>
public sealed class V0083MediaServerRefreshBacklog : SqliteSqlMigration
{
    public override int Version => 83;

    public override string Name => "media_server_refresh_backlog";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS media_server_refresh_backlog (
            media_server_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            first_failed_utc TEXT NOT NULL,
            next_attempt_utc TEXT NOT NULL,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            changes_json TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (media_server_id, media_type),
            FOREIGN KEY (media_server_id) REFERENCES media_servers(id) ON DELETE CASCADE
        );
        """;
}
