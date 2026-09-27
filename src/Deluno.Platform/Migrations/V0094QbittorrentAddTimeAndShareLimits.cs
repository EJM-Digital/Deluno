using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// The rest of #811's qBittorrent WebUI API gaps that need a saved per-client
/// setting: skip_checking, sequentialDownload and firstLastPiecePrio at
/// add-time, and setShareLimits' ratioLimit/seedingTimeLimit/
/// inactiveSeedingTimeLimit. Tags already exist (#808) and are reused rather
/// than duplicated; category/location are already sent at add-time.
/// </summary>
public sealed class V0094QbittorrentAddTimeAndShareLimits : SqliteSqlMigration
{
    public override int Version => 94;

    public override string Name => "qbittorrent_add_time_and_share_limits";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN skip_hash_check INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN sequential_download INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN first_last_piece_prio INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN share_ratio_limit REAL NULL;
        ALTER TABLE download_clients ADD COLUMN share_seeding_time_limit_minutes INTEGER NULL;
        ALTER TABLE download_clients ADD COLUMN share_inactive_seeding_time_limit_minutes INTEGER NULL;
        """;
}
