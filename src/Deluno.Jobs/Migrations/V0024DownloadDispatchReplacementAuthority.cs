using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Persists the narrow authority an acquisition decision granted to the later
/// import. A completed download is not, by itself, permission to replace a
/// library file: the dispatch must say that the title already had a file, and
/// only an explicit manual override may bypass the import-time quality guard.
/// </summary>
public sealed class V0024DownloadDispatchReplacementAuthority : SqliteSqlMigration
{
    public override int Version => 24;

    public override string Name => "download_dispatch_replacement_authority";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN replacement_authorized INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_dispatches ADD COLUMN force_replacement_authorized INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_dispatches ADD COLUMN replacement_expected_path TEXT NULL;
        """;
}
