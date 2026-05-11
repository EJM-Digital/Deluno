using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0005DispatchAlerts : SqliteSqlMigration
{
    public override int Version => 5;

    public override string Name => "dispatch_alerts";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS dispatch_alerts (
            id TEXT PRIMARY KEY,
            dispatch_id TEXT NOT NULL,
            title TEXT NOT NULL,
            summary TEXT NOT NULL,
            alert_kind TEXT NOT NULL,
            severity TEXT NOT NULL,
            metadata_json TEXT NULL,
            detected_utc TEXT NOT NULL,
            acknowledged INTEGER NOT NULL DEFAULT 0,
            acknowledged_utc TEXT NULL,
            FOREIGN KEY (dispatch_id) REFERENCES download_dispatches (id)
        );

        CREATE INDEX IF NOT EXISTS ix_dispatch_alerts_dispatch
            ON dispatch_alerts (dispatch_id);

        CREATE INDEX IF NOT EXISTS ix_dispatch_alerts_open
            ON dispatch_alerts (acknowledged)
            WHERE acknowledged = 0;

        CREATE INDEX IF NOT EXISTS ix_dispatch_alerts_severity
            ON dispatch_alerts (severity, acknowledged DESC);

        CREATE INDEX IF NOT EXISTS ix_dispatch_alerts_detected
            ON dispatch_alerts (detected_utc DESC);
        """;
}
