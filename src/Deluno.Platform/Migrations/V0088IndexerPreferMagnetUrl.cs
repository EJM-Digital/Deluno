using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A prefer-magnet-over-torrent-file toggle for torrent indexers (#752).
/// Off by default (#936); ignored for usenet indexers.
/// </summary>
public sealed class V0088IndexerPreferMagnetUrl : SqliteSqlMigration
{
    public override int Version => 88;

    public override string Name => "indexer_prefer_magnet_url";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN prefer_magnet_url INTEGER NOT NULL DEFAULT 0;";
}
