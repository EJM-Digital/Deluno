using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0024LibraryWorkflowCleanup : SqliteSqlMigration
{
    public override int Version => 24;

    public override string Name => "library_workflow_cleanup";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN cleanup_mode TEXT NOT NULL DEFAULT 'keep-source';
        ALTER TABLE libraries ADD COLUMN remove_empty_source_folders INTEGER NOT NULL DEFAULT 0;
        """;
}
