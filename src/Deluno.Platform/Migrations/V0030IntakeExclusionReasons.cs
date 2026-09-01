using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Keeps the human explanation beside an exclusion. The timestamp answers
/// when a decision was made; the reason answers why it was made.
/// </summary>
public sealed class V0030IntakeExclusionReasons : SqliteSqlMigration
{
    public override int Version => 30;

    public override string Name => "intake_exclusion_reasons";

    protected override string Sql =>
        "ALTER TABLE intake_source_exclusions ADD COLUMN reason TEXT NOT NULL DEFAULT 'Excluded from import list by user';";
}
