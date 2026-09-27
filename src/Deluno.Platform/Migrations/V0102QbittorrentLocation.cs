using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #811's remaining gap: qBittorrent's WebUI API has a setLocation call and
/// an add-time <c>savepath</c> field, but nothing in Deluno stored a save
/// path to send. <see cref="V0094QbittorrentAddTimeAndShareLimits"/>'s own
/// comment said location was "already sent at add-time" -- it was not; only
/// category was. Null (the default) leaves qBittorrent's own default save
/// path, or its category's, exactly as before.
/// </summary>
public sealed class V0102QbittorrentLocation : SqliteSqlMigration
{
    public override int Version => 102;

    public override string Name => "qbittorrent_location";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN location TEXT NULL;
        """;
}
