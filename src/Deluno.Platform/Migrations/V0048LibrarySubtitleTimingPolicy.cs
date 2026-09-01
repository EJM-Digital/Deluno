using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Stores per-library automatic subtitle timing-repair choices.</summary>
public sealed class V0048LibrarySubtitleTimingPolicy : SqliteSqlMigration
{
    public override int Version => 48;

    public override string Name => "library_subtitle_timing_policy";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN subtitle_timing_policy_json TEXT NOT NULL DEFAULT '';";
}
