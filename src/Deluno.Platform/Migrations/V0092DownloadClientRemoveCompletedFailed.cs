using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr have separate, per-download-client Remove Completed and
/// Remove Failed toggles under Completed Download Handling. Deluno had one
/// platform-wide RemoveCompletedDownloads flag applying identically to every
/// client, with no distinct Remove Failed setting at all (#806).
///
/// Both default off (#936: nothing automatic is on by default) rather than
/// inheriting the old global flag, so an existing install's behaviour does
/// not silently start deleting downloads it did not before.
/// </summary>
public sealed class V0092DownloadClientRemoveCompletedFailed : SqliteSqlMigration
{
    public override int Version => 92;

    public override string Name => "download_client_remove_completed_failed";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN remove_completed INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN remove_failed INTEGER NOT NULL DEFAULT 0;
        """;
}
