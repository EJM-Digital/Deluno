using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Logins Deluno generated for the tools it installed (#506) - a qBittorrent
/// Web UI password, a SABnzbd API key, a Weir administrator - kept so the
/// owner can open those tools, and protected like every other secret.
/// </summary>
public sealed class V0060CompanionToolLogins : SqliteSqlMigration
{
    public override int Version => 60;

    public override string Name => "companion_tool_logins";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS companion_tool_logins (
            tool_id TEXT PRIMARY KEY,
            url TEXT NOT NULL,
            username TEXT NULL,
            secret TEXT NOT NULL,
            secret_label TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;
}
