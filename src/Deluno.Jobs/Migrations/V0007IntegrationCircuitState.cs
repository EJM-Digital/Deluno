using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0007IntegrationCircuitState : SqliteSqlMigration
{
    public override int Version => 7;
    public override string Name => "integration_circuit_state";
    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS integration_circuit_states (
            integration_key TEXT PRIMARY KEY,
            circuit_open_until_utc TEXT NOT NULL,
            opened_utc TEXT NOT NULL,
            failure_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_integration_circuit_states_expiry
            ON integration_circuit_states (circuit_open_until_utc);
        """;
}
