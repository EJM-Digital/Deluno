using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// When a dispatch alert stopped describing a live problem, and why.
///
/// <para>An alert used to be open until a person acknowledged it, whatever had
/// happened to the download since. On the E2E rig one film was imported, and
/// the poller then raised "Download not detected in qBittorrent" about it three
/// times over the next five hours. Each counted as an open alert, and each also
/// added an activity entry and a recovery case on the film saying the download
/// never reached the client. Software meant to run unattended cannot need
/// someone to clear warnings about problems that fixed themselves.</para>
///
/// <para>Resolution is kept apart from acknowledgement so the record still says
/// which alerts a person looked at and which the system closed because the
/// download moved on.</para>
/// </summary>
public sealed class V0031DispatchAlertResolution : SqliteSqlMigration
{
    public override int Version => 31;

    public override string Name => "dispatch_alert_resolution";

    protected override string Sql =>
        """
        ALTER TABLE dispatch_alerts ADD COLUMN resolved_utc TEXT NULL;
        ALTER TABLE dispatch_alerts ADD COLUMN resolved_reason TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_dispatch_alerts_unresolved
            ON dispatch_alerts (dispatch_id, alert_kind)
            WHERE acknowledged = 0 AND resolved_utc IS NULL;
        """;
}
