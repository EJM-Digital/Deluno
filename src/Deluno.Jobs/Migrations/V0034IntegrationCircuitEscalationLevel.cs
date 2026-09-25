using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// A dead indexer or download client re-opened its circuit every minute
/// forever: nothing recorded how many times in a row it had already failed,
/// so nothing could make the next break longer than the last (#732).
///
/// <para>This column is how many consecutive openings a circuit has been
/// through with no success in between -- the rung on
/// <c>IntegrationResiliencePolicy.EscalatingBreakDurations</c> its next
/// opening will use. A row from before this migration has never escalated,
/// so it starts back at the first rung, same as a circuit that has never
/// opened at all.</para>
/// </summary>
public sealed class V0034IntegrationCircuitEscalationLevel : SqliteSqlMigration
{
    public override int Version => 34;

    public override string Name => "integration_circuit_escalation_level";

    protected override string Sql =>
        "ALTER TABLE integration_circuit_states ADD COLUMN escalation_level INTEGER NOT NULL DEFAULT 0;";
}
