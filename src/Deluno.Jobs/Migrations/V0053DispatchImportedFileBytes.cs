using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1189: the size, in bytes, of the file an import placed. A later check (the wrong-episode repair) compares it with
/// the file now at that path, so an import that no longer produced the file cannot decide what the file is.
/// </summary>
public sealed class V0053DispatchImportedFileBytes : SqliteSqlMigration
{
    public override int Version => 53;

    public override string Name => "dispatch_imported_file_bytes";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN imported_file_bytes INTEGER NULL;
        """;
}
