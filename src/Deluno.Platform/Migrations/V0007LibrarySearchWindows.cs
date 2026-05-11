using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0007LibrarySearchWindows : SqliteSqlMigration
{
    public override int Version => 7;

    public override string Name => "library_search_windows";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN search_window_start_hour INTEGER NULL;
        ALTER TABLE libraries ADD COLUMN search_window_end_hour INTEGER NULL;
        """;
}
