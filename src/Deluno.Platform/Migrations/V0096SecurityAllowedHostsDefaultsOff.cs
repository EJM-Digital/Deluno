using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #939's Allowed Hosts reads a missing <c>security.allowedHosts.enabled</c>
/// key as off already, so a brand-new install needs no row here -- it takes
/// today's behaviour (answer on any address) exactly as every earlier
/// release did, until the setup guide or the Security page turns it on
/// (#936).
///
/// <para>This migration exists for an installed database that already holds
/// other settings: it writes an explicit <c>false</c> the first time it runs,
/// the same "existing install, freeze it, never silently flip" shape
/// <see cref="V0073AutomationDefaultsOff"/> uses. It is a belt-and-braces
/// statement of intent rather than a behaviour change -- absent and
/// explicit-false already read identically -- but it means an existing
/// install's row set visibly says "this was decided off", not "this was
/// never decided".</para>
/// </summary>
public sealed class V0096SecurityAllowedHostsDefaultsOff : SqliteSqlMigration
{
    private const string EnabledKey = "security.allowedHosts.enabled";

    public override int Version => 96;

    public override string Name => "security_allowed_hosts_defaults_off";

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{EnabledKey}', 'false', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{EnabledKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key != '{EnabledKey}'
          );
        """;
}
