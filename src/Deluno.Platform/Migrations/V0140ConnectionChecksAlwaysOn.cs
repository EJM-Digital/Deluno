using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1432: the regular connection check always runs, so the old "Check connections regularly" choice is gone.
/// An install that had turned it off has its stored answer removed, so nothing keeps reading it.
/// </summary>
public sealed class V0140ConnectionChecksAlwaysOn : SqliteSqlMigration
{
    public override int Version => 140;

    public override string Name => "connection_checks_always_on";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'connections.checkRegularly';
        """;
}
