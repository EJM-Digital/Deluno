using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0006DownloadRetryTracking : SqliteSqlMigration
{
    public override int Version => 6;
    public override string Name => "download_retry_tracking";
    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0;
        """;
}
