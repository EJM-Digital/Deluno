using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Keeps the terminal import failure as the same typed, attributable contract
/// used by the external client that produced the dispatch. The legacy code and
/// message columns remain for older consumers and migration compatibility.
/// </summary>
public sealed class V0022DownloadDispatchImportFailureDetails : SqliteSqlMigration
{
    public override int Version => 22;

    public override string Name => "download_dispatch_import_failure_details";

    protected override string Sql =>
        "ALTER TABLE download_dispatches ADD COLUMN import_failure_json TEXT NULL;";
}
