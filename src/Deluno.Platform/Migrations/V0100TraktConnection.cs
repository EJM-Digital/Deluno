using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Trakt OAuth (#846): a single row (Deluno is single-user) holding the
/// registered app's Client ID/Secret and the current token pair, all
/// encrypted at rest through the same secret-protection mechanism used for
/// every other stored credential (see <c>seerr_servers.api_key</c>,
/// <c>metadata.tmdbApiKey</c>).
/// </summary>
public sealed class V0100TraktConnection : SqliteSqlMigration
{
    public override int Version => 100;

    public override string Name => "trakt_connection";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS trakt_connection (
            id TEXT PRIMARY KEY,
            client_id TEXT NULL,
            client_secret TEXT NULL,
            access_token TEXT NULL,
            refresh_token TEXT NULL,
            token_expires_utc TEXT NULL,
            needs_reconnect INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;
}
