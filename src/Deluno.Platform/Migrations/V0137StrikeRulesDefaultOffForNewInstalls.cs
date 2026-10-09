using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1194: a brand-new install has no strike rule on and no acting cleanup switch. Until now a missing
/// <c>download-health.noProgress.enabled</c> read as on, and a missing <c>cleanup.blockReleaseAfterThreshold</c> or
/// <c>cleanup.queueReplacementAfterThreshold</c> read as on, so a fresh database got an enabled "No progress" rule
/// that blocked the release and searched again. Those three now read off when absent, so an install that already
/// existed is frozen at what it does today: each key is written as an explicit <c>true</c> if it is still absent,
/// but only when the settings table already held something else (the same "database with no other row is brand
/// new" test as V0073 and V0113).
/// </summary>
public sealed class V0137StrikeRulesDefaultOffForNewInstalls : SqliteSqlMigration
{
    public override int Version => 137;

    public override string Name => "strike_rules_default_off_for_new_installs";

    protected override string Sql =>
        """
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT 'download-health.noProgress.enabled', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = 'download-health.noProgress.enabled')
          AND EXISTS (SELECT 1 FROM system_settings WHERE setting_key != 'download-health.noProgress.enabled');

        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT 'cleanup.blockReleaseAfterThreshold', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = 'cleanup.blockReleaseAfterThreshold')
          AND EXISTS (SELECT 1 FROM system_settings WHERE setting_key NOT IN ('cleanup.blockReleaseAfterThreshold', 'download-health.noProgress.enabled'));

        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT 'cleanup.queueReplacementAfterThreshold', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = 'cleanup.queueReplacementAfterThreshold')
          AND EXISTS (SELECT 1 FROM system_settings WHERE setting_key NOT IN ('cleanup.queueReplacementAfterThreshold', 'cleanup.blockReleaseAfterThreshold', 'download-health.noProgress.enabled'));
        """;
}
