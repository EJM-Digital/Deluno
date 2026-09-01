using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Links a mutable quality profile to the immutable typed plan it uses.</summary>
public sealed class V0045QualityProfilePlanReference : SqliteSqlMigration
{
    public override int Version => 45;

    public override string Name => "quality_profile_plan_reference";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN release_preference_plan_json TEXT NULL;";
}
