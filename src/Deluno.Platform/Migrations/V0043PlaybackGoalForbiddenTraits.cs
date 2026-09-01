using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Stores explicit playback traits that a goal must not require.</summary>
public sealed class V0043PlaybackGoalForbiddenTraits : SqliteSqlMigration
{
    public override int Version => 43;

    public override string Name => "playback_goal_forbidden_traits";

    protected override string Sql =>
        "ALTER TABLE playback_goals ADD COLUMN forbidden_trait_ids_json TEXT NOT NULL DEFAULT '[]';";
}
