using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #936 reverses the "on unless explicitly turned off" reading of
/// <c>media.renameOnImport</c> back to "off unless explicitly turned on". A
/// brand-new install now gets this off simply because the key is absent and
/// <c>SqlitePlatformSettingsRepository</c> reads a missing key as off.
///
/// <para>An install that already existed before this migration ran was
/// relying on the old "missing key = on" reading, so this migration writes
/// an explicit <c>true</c> for the key if it is still absent — but only when
/// the settings table already held something else first. A database with no
/// other setting row yet is treated as brand new (never configured), and
/// gets no explicit write: it reads off, which is exactly what a new install
/// should read. See the #936 exec notes / PR description for the edge case
/// this does not cover (an old install that never once opened the Settings
/// screen or ran the setup guide, and so has zero rows here too) — accepted
/// as a documented, narrow gap rather than a reason to build a dedicated
/// first-run marker for it.</para>
///
/// <para><b>Only <c>media.renameOnImport</c> is frozen here.</b> An earlier
/// version of this migration also froze
/// <c>cleanup.removeClientEntryAfterThreshold</c> and
/// <c>cleanup.purgePayloadAfterThreshold</c> to "true" for existing installs,
/// which was wrong: <c>v1.0.0-rc.22</c> — the last release — already read
/// both of those as OFF when the key was missing
/// (<c>string.Equals(value, "true", ...)</c>, not the "unless false"
/// shape <c>media.renameOnImport</c> uses). #904's "default on" change for
/// those two (commit 2e9f2125) never shipped in a release, so there is no
/// existing install anywhere whose effective behaviour depended on it.
/// Freezing them to "true" here would have switched removal and deletion on
/// for every existing install that never explicitly saved either key —
/// exactly the silent flip this migration exists to prevent, just aimed at
/// the wrong value. Nothing needs to be written for either key: a missing
/// value already reads as off both before and after #936.</para>
/// </summary>
public sealed class V0073AutomationDefaultsOff : SqliteSqlMigration
{
    private const string RenameOnImportKey = "media.renameOnImport";

    public override int Version => 73;

    public override string Name => "automation_defaults_off";

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{RenameOnImportKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{RenameOnImportKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key != '{RenameOnImportKey}'
          );
        """;
}
