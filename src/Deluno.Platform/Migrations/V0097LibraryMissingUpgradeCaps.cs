using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Splits the single MaxItemsPerRun cap into a missing-item cap and an
/// upgrade-item cap that a library can tune independently (#830) — turning up
/// how many upgrade candidates a cycle works through no longer also turns up
/// how many missing candidates it works through, and vice versa.
///
/// <para>Every existing library keeps its current MaxItemsPerRun value
/// applied to both new caps, so nothing changes for it until it is
/// re-tuned.</para>
/// </summary>
public sealed class V0097LibraryMissingUpgradeCaps : SqliteSqlMigration
{
    public override int Version => 97;

    public override string Name => "library_missing_upgrade_caps";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN max_missing_items_per_run INTEGER NOT NULL DEFAULT 25;
        ALTER TABLE libraries ADD COLUMN max_upgrade_items_per_run INTEGER NOT NULL DEFAULT 25;
        UPDATE libraries SET max_missing_items_per_run = max_items_per_run, max_upgrade_items_per_run = max_items_per_run;
        """;
}
