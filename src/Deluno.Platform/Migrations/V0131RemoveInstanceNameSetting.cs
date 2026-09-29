using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1075: the Instance name setting is gone; Deluno always uses the machine's
/// hostname. Removes the stored value so nothing reads it again.
/// </summary>
public sealed class V0131RemoveInstanceNameSetting : SqliteSqlMigration
{
    public override int Version => 131;

    public override string Name => "remove_instance_name_setting";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'app.instanceName';
        """;
}
