using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A list source can require a movie to reach a minimum release stage
/// (Announced/In Cinemas/Released) before Deluno searches for it (#849),
/// mirroring the gate movie collections and manual adds already have.
///
/// <para>'announced' always passes, so every existing source keeps searching
/// immediately on add — exactly today's behaviour — until it opts into a
/// later stage.</para>
/// </summary>
public sealed class V0099IntakeSourceMinimumAvailability : SqliteSqlMigration
{
    public override int Version => 99;

    public override string Name => "intake_source_minimum_availability";

    protected override string Sql =>
        "ALTER TABLE intake_sources ADD COLUMN minimum_availability TEXT NOT NULL DEFAULT 'announced';";
}
