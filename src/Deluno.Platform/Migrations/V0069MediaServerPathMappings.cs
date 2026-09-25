using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Per-server path mapping between Deluno's own filesystem view and a media
/// server's (#662) - the single most common Plex/Jellyfin/Emby Connect
/// misconfiguration on Docker and NAS setups, per the parity audit. Same
/// shape as <c>download_client_path_mappings</c> (V0017), the direction
/// reversed: Deluno's path in, what the server calls that folder out.
/// </summary>
public sealed class V0069MediaServerPathMappings : SqliteSqlMigration
{
    public override int Version => 69;

    public override string Name => "media_server_path_mappings";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS media_server_path_mappings (
            id TEXT PRIMARY KEY,
            media_server_id TEXT NOT NULL,
            deluno_path TEXT NOT NULL,
            server_path TEXT NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            priority INTEGER NOT NULL DEFAULT 10,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (media_server_id) REFERENCES media_servers(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_media_server_path_mappings_server_deluno
            ON media_server_path_mappings (media_server_id, deluno_path);

        CREATE INDEX IF NOT EXISTS ix_media_server_path_mappings_server_priority
            ON media_server_path_mappings (media_server_id, is_enabled, priority);
        """;
}
