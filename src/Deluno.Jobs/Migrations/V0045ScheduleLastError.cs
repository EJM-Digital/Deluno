using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: why a scheduled task last failed, in everyday words, so the System screen can say it under
/// the task's name. Null when the last run did not fail.
/// </summary>
public sealed class V0045ScheduleLastError : SqliteSqlMigration
{
    public override int Version => 45;

    public override string Name => "schedule_last_error";

    protected override string Sql =>
        "ALTER TABLE worker_schedule_state ADD COLUMN last_error TEXT NULL;";
}
