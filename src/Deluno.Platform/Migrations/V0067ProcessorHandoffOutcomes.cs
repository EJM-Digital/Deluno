using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// What Deluno owes a processor about each hand-off, written down so it can be
/// said again until it is heard (#580).
///
/// <para>A table of its own rather than columns on <c>processor_handoffs</c>: a
/// new download of the same release restarts that row under a new id, and the
/// processor still keys the earlier outcome on the earlier id.</para>
/// </summary>
public sealed class V0067ProcessorHandoffOutcomes : SqliteSqlMigration
{
    public override int Version => 67;

    public override string Name => "processor_handoff_outcomes";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS processor_handoff_outcomes (
            handoff_id TEXT PRIMARY KEY,
            processor_name TEXT NOT NULL,
            library_id TEXT NOT NULL,
            release_name TEXT NOT NULL,
            outcome TEXT NOT NULL,
            imported_path TEXT NULL,
            reason TEXT NULL,
            occurred_utc TEXT NOT NULL,
            delivery TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_attempt_utc TEXT NULL,
            next_attempt_utc TEXT NULL,
            released INTEGER NOT NULL DEFAULT 0,
            processor_said TEXT NULL,
            recorded_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_processor_handoff_outcomes_due
            ON processor_handoff_outcomes (delivery, next_attempt_utc);
        """;
}
