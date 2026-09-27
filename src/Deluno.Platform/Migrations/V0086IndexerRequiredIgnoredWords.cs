using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Required/Ignored word lists that reject a release by keyword before grab
/// (#744), separate from the existing PreferIndexerFlags ranking hint.
/// </summary>
public sealed class V0086IndexerRequiredIgnoredWords : SqliteSqlMigration
{
    public override int Version => 86;

    public override string Name => "indexer_required_ignored_words";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN required_words TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN ignored_words TEXT NULL;
        """;
}
