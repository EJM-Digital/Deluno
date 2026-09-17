using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Leaving soon collections on the media servers (#521).
///
/// <list type="bullet">
/// <item><c>retention_marks.server_items_json</c> - the server items a mark was
/// matched to on its last check, so the collections can be filled from every
/// rule's marks without listing each library again.</item>
/// <item><c>retention_collections</c> - per server and media type, how the last
/// attempt to bring its Leaving soon collection into line went. A failure is
/// shown on the server's row and tried again at the next check.</item>
/// <item><c>media_servers.leaving_soon_on_home</c> - Plex: show the collection
/// on home.</item>
/// </list>
/// </summary>
public sealed class V0063RetentionCollections : SqliteSqlMigration
{
    public override int Version => 63;

    public override string Name => "retention_collections";

    protected override string Sql =>
        """
        ALTER TABLE retention_marks ADD COLUMN server_items_json TEXT NOT NULL DEFAULT '[]';
        ALTER TABLE media_servers ADD COLUMN leaving_soon_on_home INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS retention_collections (
            server_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            members INTEGER NOT NULL DEFAULT 0,
            skipped INTEGER NOT NULL DEFAULT 0,
            problem TEXT NULL,
            attempted_utc TEXT NOT NULL,
            synced_utc TEXT NULL,
            PRIMARY KEY (server_id, media_type)
        );
        """;
}
