using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Extends replacement authority from one movie/episode path to a typed TV
/// manifest. The JSON is an immutable dispatch-time snapshot and is always
/// revalidated against current catalogue ownership before import.
/// </summary>
public sealed class V0025DownloadDispatchReplacementManifest : SqliteSqlMigration
{
    public override int Version => 25;

    public override string Name => "download_dispatch_replacement_manifest";

    protected override string Sql =>
        "ALTER TABLE download_dispatches ADD COLUMN replacement_targets_json TEXT NULL;";
}
