using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Adds outcome information to the durable recurring-pass lease. The original
/// last_run_utc column remains the start/claim timestamp, while these columns
/// let the System screen distinguish a completed, failed or still-running pass.
/// </summary>
public sealed class V0020SystemTaskRunState : SqliteSqlMigration
{
    public override int Version => 20;

    public override string Name => "system_task_run_state";

    protected override string Sql =>
        """
        ALTER TABLE worker_schedule_state ADD COLUMN last_completed_utc TEXT NULL;
        ALTER TABLE worker_schedule_state ADD COLUMN last_result TEXT NULL;
        ALTER TABLE worker_schedule_state ADD COLUMN last_duration_ms INTEGER NULL;
        ALTER TABLE worker_schedule_state ADD COLUMN next_run_utc TEXT NULL;
        """;
}
