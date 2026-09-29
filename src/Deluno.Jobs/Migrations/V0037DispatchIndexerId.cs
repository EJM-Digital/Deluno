using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1077: a download is filed under its indexer's id, so renaming the indexer does not split its grabs and success rate. The id is filled in by the start-up backfill, which can see the indexers; a row whose name matches no indexer keeps its name only.
/// </summary>
public sealed class V0037DispatchIndexerId : SqliteSqlMigration
{
    public override int Version => 37;

    public override string Name => "dispatch_indexer_id";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN indexer_id TEXT NULL;
        CREATE INDEX IF NOT EXISTS ix_download_dispatches_indexer_id ON download_dispatches (indexer_id);
        """;
}
