using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Plex, Jellyfin and Emby servers, the first media servers Deluno talks to
/// (#493). A Connection like the others: a kind, an address, a protected token,
/// and the health a test last recorded - plus what the server called itself,
/// so the settings screen can show which machine a row really reaches.
/// </summary>
public sealed class V0058MediaServers : SqliteSqlMigration
{
    public override int Version => 58;

    public override string Name => "media_servers";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS media_servers (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            base_url TEXT NOT NULL,
            token TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            health_status TEXT NOT NULL DEFAULT 'untested',
            last_health_message TEXT NULL,
            last_health_latency_ms INTEGER NULL,
            last_health_test_utc TEXT NULL,
            last_health_failure_json TEXT NULL,
            server_name TEXT NULL,
            server_id TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;
}
