using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1118: the newer version a person dismissed for a connection ("Dismiss this version"). The notice for a
/// connection comes back only for a version newer than this one.
/// </summary>
public sealed class V0052ConnectionVersionDismissals : SqliteSqlMigration
{
    public override int Version => 52;

    public override string Name => "connection_version_dismissals";

    protected override string Sql =>
        """
        ALTER TABLE connection_versions ADD COLUMN dismissed_version TEXT NULL;
        """;
}
