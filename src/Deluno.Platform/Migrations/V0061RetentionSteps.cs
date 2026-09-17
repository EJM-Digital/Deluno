using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Retention rules become steps (#494, DESIGN-008): Keep only and Also include,
/// with blocks as brackets, at a level - movie, show, season or episode.
///
/// <para>Existing rules are not rewritten here. A rule with no steps is read as
/// its flat conditions, every one of which had to hold: a run of Keep only
/// steps, which means exactly what it meant before.</para>
/// </summary>
public sealed class V0061RetentionSteps : SqliteSqlMigration
{
    public override int Version => 61;

    public override string Name => "retention_steps";

    protected override string Sql =>
        """
        ALTER TABLE retention_rules ADD COLUMN level TEXT NULL;
        ALTER TABLE retention_rules ADD COLUMN steps_json TEXT NULL;
        """;
}
