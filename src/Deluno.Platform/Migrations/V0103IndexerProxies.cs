using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A dedicated Indexer Proxies table (#748): more than one HTTP/SOCKS4/SOCKS5/
/// FlareSolverr proxy can be configured at once, and each indexer can be
/// assigned to one of them instead of sharing a single global Cloudflare-helper
/// address. <c>indexer_sources.proxy_id</c> is nullable and left unenforced as
/// a foreign key (consistent with the rest of this table's optional columns);
/// the repository clears it itself when a proxy is deleted.
/// </summary>
public sealed class V0103IndexerProxies : SqliteSqlMigration
{
    public override int Version => 103;

    public override string Name => "indexer_proxies";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS indexer_proxies (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            proxy_type TEXT NOT NULL,
            address TEXT NOT NULL,
            username TEXT NULL,
            password TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        ALTER TABLE indexer_sources ADD COLUMN proxy_id TEXT NULL;
        """;
}
