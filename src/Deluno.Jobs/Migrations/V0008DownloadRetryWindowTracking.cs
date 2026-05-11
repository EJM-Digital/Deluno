using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0008DownloadRetryWindowTracking : SqliteSqlMigration
{
    public override int Version => 8;

    public override string Name => "download_retry_window_tracking";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN next_retry_eligible_utc TEXT NULL;
        """;
}
