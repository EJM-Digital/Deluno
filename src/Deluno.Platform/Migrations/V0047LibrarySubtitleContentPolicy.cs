using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores the named, deterministic subtitle cleanups a library applies after
/// download. Empty remains the default so existing libraries keep provider
/// bytes unchanged.
/// </summary>
public sealed class V0047LibrarySubtitleContentPolicy : SqliteSqlMigration
{
    public override int Version => 47;

    public override string Name => "library_subtitle_content_policy";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN subtitle_content_policy_json TEXT NOT NULL DEFAULT '';";
}
