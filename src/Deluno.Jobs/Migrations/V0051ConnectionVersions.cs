using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1117: the version each connection last reported (qBittorrent 4.6.7, Plex 1.43.4, Weir 4.0.0 ...), one row
/// per connection, written by its checks and read by the Connections screens.
/// </summary>
public sealed class V0051ConnectionVersions : SqliteSqlMigration
{
    public override int Version => 51;

    public override string Name => "connection_versions";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS connection_versions (
            kind TEXT NOT NULL,
            connection_id TEXT NOT NULL,
            project TEXT NULL,
            version TEXT NOT NULL,
            read_utc TEXT NOT NULL,
            PRIMARY KEY (kind, connection_id)
        );
        """;
}
