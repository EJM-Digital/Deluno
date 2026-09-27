using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A separate anime category list per indexer (#742): anime is often
/// catalogued under different category numbers than normal TV on the same
/// indexer.
/// </summary>
public sealed class V0080IndexerAnimeCategories : SqliteSqlMigration
{
    public override int Version => 80;

    public override string Name => "indexer_anime_categories";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN anime_categories TEXT NULL;";
}
