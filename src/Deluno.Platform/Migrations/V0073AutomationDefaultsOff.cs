using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #936 reverses the "on unless explicitly turned off" reading of three
/// settings (rename on import, remove-from-client, delete-payload — #904's
/// change to the last two) back to "off unless explicitly turned on". A
/// brand-new install now gets these off simply because the key is absent and
/// <c>SqlitePlatformSettingsRepository</c> reads a missing key as off.
///
/// <para>An install that already existed before this migration ran was
/// relying on the old "missing key = on" reading, so this migration writes
/// an explicit <c>true</c> for any of the three keys that is still absent —
/// but only when the settings table already held something else first. A
/// database with no other setting row yet is treated as brand new (never
/// configured), and gets no explicit write: it reads off, which is exactly
/// what a new install should read. See the #936 exec notes / PR description
/// for the edge case this does not cover (an old install that never once
/// opened the Settings screen or ran the setup guide, and so has zero rows
/// here too) — accepted as a documented, narrow gap rather than a reason to
/// build a dedicated first-run marker for it.</para>
/// </summary>
public sealed class V0073AutomationDefaultsOff : SqliteSqlMigration
{
    private const string RenameOnImportKey = "media.renameOnImport";
    private const string RemoveClientEntryKey = "cleanup.removeClientEntryAfterThreshold";
    private const string PurgePayloadKey = "cleanup.purgePayloadAfterThreshold";

    public override int Version => 73;

    public override string Name => "automation_defaults_off";

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{RenameOnImportKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{RenameOnImportKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key NOT IN ('{RenameOnImportKey}', '{RemoveClientEntryKey}', '{PurgePayloadKey}')
          );

        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{RemoveClientEntryKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{RemoveClientEntryKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key NOT IN ('{RenameOnImportKey}', '{RemoveClientEntryKey}', '{PurgePayloadKey}')
          );

        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{PurgePayloadKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{PurgePayloadKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key NOT IN ('{RenameOnImportKey}', '{RemoveClientEntryKey}', '{PurgePayloadKey}')
          );
        """;
}
