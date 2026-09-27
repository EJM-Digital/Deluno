using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr have separate, per-download-client Remove Completed and
/// Remove Failed toggles under Completed Download Handling. Deluno had one
/// platform-wide RemoveCompletedDownloads flag (<c>media.removeCompletedDownloads</c>)
/// applying identically to every client, with no distinct Remove Failed
/// setting at all (#806).
///
/// Remove Failed defaults off for every install: there was never a
/// platform-wide equivalent, so off is both the #936 new-install default and
/// the only behaviour any existing install ever had. Remove Completed
/// defaults off for a brand-new install (#936), but an existing install that
/// had already turned the old platform-wide flag on must keep removing
/// completed downloads across every one of its clients — the acceptance bar
/// for #806 is "behaviour does not change on upgrade", not "everyone starts
/// clean". The UPDATE below runs before <see cref="V0093DropGlobalRemoveCompletedDownloads"/>
/// deletes the old key, so the old value is still there to read.
/// </summary>
public sealed class V0092DownloadClientRemoveCompletedFailed : SqliteSqlMigration
{
    public override int Version => 92;

    public override string Name => "download_client_remove_completed_failed";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN remove_completed INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN remove_failed INTEGER NOT NULL DEFAULT 0;
        UPDATE download_clients SET remove_completed = 1
        WHERE EXISTS (
            SELECT 1 FROM system_settings
            WHERE setting_key = 'media.removeCompletedDownloads' AND setting_value = 'true'
        );
        """;
}
