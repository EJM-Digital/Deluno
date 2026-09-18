using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Weir is now Weir (#529). The login Deluno generated for it was kept under
/// the tool id <c>weir</c>; it moves to <c>weir</c> so an install that set
/// it up before the rename still shows and uses it. The secret is protected
/// under a purpose shared by every companion tool, so it moves as it is.
///
/// <para>A <c>weir</c> row, if one somehow exists already, wins: it was written
/// after the rename and is the newer login.</para>
/// </summary>
public sealed class V0065CompanionToolWeir : SqliteSqlMigration
{
    public override int Version => 65;

    public override string Name => "companion_tool_weir";

    protected override string Sql =>
        """
        UPDATE companion_tool_logins
        SET tool_id = 'weir'
        WHERE tool_id = 'weir'
          AND NOT EXISTS (SELECT 1 FROM companion_tool_logins WHERE tool_id = 'weir');

        DELETE FROM companion_tool_logins WHERE tool_id = 'weir';
        """;
}
