using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr let a torrent client's Initial State add a grab paused
/// instead of active, for queueing large batches by hand (#805). Deluno's
/// grab request and every client adapter always sent an active add.
/// </summary>
public sealed class V0072DownloadClientAddPaused : SqliteSqlMigration
{
    public override int Version => 72;

    public override string Name => "download_client_add_paused";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN add_paused INTEGER NOT NULL DEFAULT 0;
        """;
}
