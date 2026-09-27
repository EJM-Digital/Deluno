using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A per-indexer season-pack search toggle (#743): restricts TV searches
/// against this indexer to season packs only, episodes only, or both
/// (default). Null/"both" means no restriction.
/// </summary>
public sealed class V0091IndexerSeasonPackSearchMode : SqliteSqlMigration
{
    public override int Version => 91;

    public override string Name => "indexer_season_pack_search_mode";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN season_pack_search_mode TEXT NULL;";
}
