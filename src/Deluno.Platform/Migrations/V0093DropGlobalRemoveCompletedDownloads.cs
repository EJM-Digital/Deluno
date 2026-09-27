using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// The platform-wide RemoveCompletedDownloads flag is dead (#806): it is
/// replaced by the per-client RemoveCompleted/RemoveFailed toggles added by
/// V0092, and nothing reads the old key any more. Dropped rather than left
/// behind so a stale value cannot be mistaken for a live setting.
/// </summary>
public sealed class V0093DropGlobalRemoveCompletedDownloads : SqliteSqlMigration
{
    public override int Version => 93;

    public override string Name => "drop_global_remove_completed_downloads";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'media.removeCompletedDownloads';
        """;
}
