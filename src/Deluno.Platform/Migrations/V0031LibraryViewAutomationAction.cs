using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a saved library view opt into an existing library automation pass.
/// The action is deliberately nullable: null keeps a view presentation-only.
/// </summary>
public sealed class V0031LibraryViewAutomationAction : SqliteSqlMigration
{
    public override int Version => 31;

    public override string Name => "library_view_automation_action";

    protected override string Sql =>
        "ALTER TABLE library_views ADD COLUMN automation_action TEXT NULL;\n"
        + "CREATE INDEX IF NOT EXISTS ix_library_views_automation_action\n"
        + "    ON library_views (variant, library_id, automation_action);";
}
