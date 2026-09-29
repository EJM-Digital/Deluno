using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1015: a list can give tags to every title it adds (Radarr and Sonarr's
/// import list Tags). Empty for every existing list, so nothing changes until
/// someone chooses tags.
/// </summary>
public sealed class V0127IntakeSourceTags : SqliteSqlMigration
{
    public override int Version => 127;

    public override string Name => "intake_source_tags";

    protected override string Sql =>
        """
        ALTER TABLE intake_sources ADD COLUMN tags TEXT NOT NULL DEFAULT '';
        """;
}
