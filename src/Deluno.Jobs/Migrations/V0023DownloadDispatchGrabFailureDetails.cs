using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Keeps the normalized failure returned by a download-client grab beside
/// the legacy code/message columns. The JSON is deliberately additive so
/// older integrations can continue reading the original dispatch shape while
/// queue, activity, and restart recovery retain service attribution.
/// </summary>
public sealed class V0023DownloadDispatchGrabFailureDetails : SqliteSqlMigration
{
    public override int Version => 23;

    public override string Name => "download_dispatch_grab_failure_details";

    protected override string Sql =>
        "ALTER TABLE download_dispatches ADD COLUMN grab_failure_json TEXT NULL;";
}
