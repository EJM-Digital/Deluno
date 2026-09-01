using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores the typed scenario/automation intent that accompanies a Media Plan.
/// It remains separate from notes so preview, history and future runtime
/// adapters can reason about each intent without parsing prose.
/// </summary>
public sealed class V0044MediaPlanAutomationIntent : SqliteSqlMigration
{
    public override int Version => 44;

    public override string Name => "media_plan_automation_intent";

    protected override string Sql =>
        "ALTER TABLE policy_sets ADD COLUMN automation_intent_json TEXT NULL;";
}
