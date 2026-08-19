using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Persists the recurring background passes (dispatch cleanup, metadata refresh
/// automation, etc.) so their schedule survives a restart instead of resetting
/// to "never run" every time the process starts, and so two hosts sharing one
/// database cannot both claim the same pass.
/// </summary>
public sealed class V0011WorkerScheduleState : SqliteSqlMigration
{
    public override int Version => 11;

    public override string Name => "worker_schedule_state";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS worker_schedule_state (
            schedule_key TEXT PRIMARY KEY,
            last_run_utc TEXT NOT NULL
        );
        """;
}
