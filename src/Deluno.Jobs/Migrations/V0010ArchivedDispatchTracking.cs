using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Records the point at which a completed dispatch leaves the active queue.
/// Existing databases must receive this before polling queries archived_utc.
/// </summary>
public sealed class V0010ArchivedDispatchTracking : SqliteSqlMigration
{
    public override int Version => 10;

    public override string Name => "archived_dispatch_tracking";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN archived_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_completed_unarchived
            ON download_dispatches (import_status, archived_utc, import_detected_utc DESC)
            WHERE import_status = 'completed' AND archived_utc IS NULL;
        """;
}
