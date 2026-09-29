using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #999: a per-client qBittorrent content layout (default, original,
/// subfolder, no-subfolder). Every existing client, and every new one until
/// someone chooses, is "default": nothing is sent, so qBittorrent keeps
/// using its own setting exactly as before.
/// </summary>
public sealed class V0126DownloadClientContentLayout : SqliteSqlMigration
{
    public override int Version => 126;

    public override string Name => "download_client_content_layout";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN content_layout TEXT NOT NULL DEFAULT 'default';
        """;
}
