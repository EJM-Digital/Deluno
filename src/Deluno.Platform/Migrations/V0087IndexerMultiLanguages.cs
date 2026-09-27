using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A per-indexer multi-language release meaning (#746): what a MULTI tag on
/// this indexer's releases actually names, e.g. "Original, French". Null
/// keeps Deluno's fixed "Multilingual" default.
/// </summary>
public sealed class V0087IndexerMultiLanguages : SqliteSqlMigration
{
    public override int Version => 87;

    public override string Name => "indexer_multi_languages";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN multi_languages TEXT NULL;";
}
