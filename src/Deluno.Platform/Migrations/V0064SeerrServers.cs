using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Seerr (#522).
///
/// <list type="bullet">
/// <item><c>seerr_servers</c> - Jellyseerr and Overseerr servers: address,
/// encrypted API key and health, as a media server has.</item>
/// <item><c>retention_seerr_cleanups</c> - a title a rule removed whose Seerr
/// record could not be cleared yet. It is tried again at later checks and
/// reported until it is gone.</item>
/// </list>
/// </summary>
public sealed class V0064SeerrServers : SqliteSqlMigration
{
    public override int Version => 64;

    public override string Name => "seerr_servers";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS seerr_servers (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            base_url TEXT NOT NULL,
            api_key TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            health_status TEXT NOT NULL DEFAULT 'untested',
            last_health_message TEXT NULL,
            last_health_latency_ms INTEGER NULL,
            last_health_test_utc TEXT NULL,
            server_name TEXT NULL,
            version TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS retention_seerr_cleanups (
            server_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            title TEXT NOT NULL,
            tmdb_id TEXT NULL,
            tvdb_id TEXT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            problem TEXT NULL,
            created_utc TEXT NOT NULL,
            attempted_utc TEXT NOT NULL,
            PRIMARY KEY (server_id, entity_id)
        );
        """;
}
