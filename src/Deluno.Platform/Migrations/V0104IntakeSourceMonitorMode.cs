using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A list source can choose how much of a newly added title to actually
/// watch for (#848) — All/Future/Missing/Existing/Recent for TV,
/// Movie/None for movies — instead of Deluno always fully monitoring
/// everything a list adds.
///
/// Every existing source defaults to 'all', exactly the hardcoded
/// fully-monitored behaviour every list-added title had before this column
/// existed.
/// </summary>
public sealed class V0104IntakeSourceMonitorMode : SqliteSqlMigration
{
    public override int Version => 104;

    public override string Name => "intake_source_monitor_mode";

    protected override string Sql =>
        "ALTER TABLE intake_sources ADD COLUMN monitor_mode TEXT NOT NULL DEFAULT 'all';";
}
