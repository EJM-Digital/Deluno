using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// The cadence a recurring pass was actually claimed at.
///
/// <para>The System screen read its interval from the declaration in
/// <c>SystemTasks</c>, which is right for the fixed engineering cadences and
/// wrong for the configurable ones: it printed "6h · configured" beside the
/// library file check whatever the user had chosen. Recording what the
/// scheduler used means the screen reports the truth for every pass rather
/// than for most of them.</para>
///
/// <para>Null until a pass has been claimed once, in which case the declared
/// interval is still the best answer available.</para>
/// </summary>
public sealed class V0030ScheduleIntervalUsed : SqliteSqlMigration
{
    public override int Version => 30;

    public override string Name => "schedule_interval_used";

    protected override string Sql =>
        "ALTER TABLE worker_schedule_state ADD COLUMN interval_seconds INTEGER NULL;";
}
