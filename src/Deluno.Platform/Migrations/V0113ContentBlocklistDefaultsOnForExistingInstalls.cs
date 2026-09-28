using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #944: before #816's content blocklist existed, the dangerous-file warning
/// (a suspicious release-name extension) was unconditional. #816 made the
/// check configurable and, following #936, off by default -- which silently
/// took the warning away from every install that already existed, because a
/// missing <c>download-health.content-blocklist.v1</c> key reads as
/// <see cref="ContentBlocklistSettings.Default"/> (<c>Enabled: false</c>).
///
/// <para>This freezes an existing install to the pre-#816 behaviour by
/// writing <see cref="ContentBlocklistSettings.Default"/> with
/// <c>Enabled: true</c> the first time it runs -- but only when the settings
/// table already held something else first, the same "existing install,
/// never silently flip" shape <see cref="V0073AutomationDefaultsOff"/> and
/// <see cref="V0096SecurityAllowedHostsDefaultsOff"/> use. A database with no
/// other setting row yet is a brand-new install: it gets no explicit write,
/// keeps reading <c>Enabled: false</c>, and is asked about it in the setup
/// guide instead (#936).</para>
/// </summary>
public sealed class V0113ContentBlocklistDefaultsOnForExistingInstalls : SqliteSqlMigration
{
    private const string SettingKey = "download-health.content-blocklist.v1";

    public override int Version => 113;

    public override string Name => "content_blocklist_defaults_on_for_existing_installs";

    /// <summary>
    /// Matches exactly what <c>SqliteDownloadHealthRepository</c> writes for
    /// <see cref="ContentBlocklistSettings"/> (plain <c>JsonSerializer.Serialize</c>,
    /// no custom naming policy) so a later read deserializes it the same way
    /// a normal save would.
    /// </summary>
    private static readonly string DefaultEnabledJson =
        JsonSerializer.Serialize(ContentBlocklistSettings.Default with { Enabled = true })
            .Replace("'", "''");

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{SettingKey}', '{DefaultEnabledJson}', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{SettingKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key != '{SettingKey}'
          );
        """;
}
