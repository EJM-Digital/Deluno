using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A search source's own answer to "how long do I have to keep sharing this?" (#288).
///
/// The requirement comes from the site a release was taken from, not from the
/// library it landed in: a movie from a private tracker must keep sharing, and
/// the same movie from a public one need not. That is the piece Radarr does not
/// model, and the reason its users reach for a second tool to decide what is
/// safe to delete.
///
/// Every column is nullable and means "inherit the global setting". A source
/// only has to state what makes it different, so an install with one rule for
/// everything never writes a value here at all.
/// </summary>
public sealed class V0025IndexerSharingPolicy : SqliteSqlMigration
{
    public override int Version => 25;

    public override string Name => "indexer_sharing_policy";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN sharing_mode TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN sharing_for_hours INTEGER NULL;
        ALTER TABLE indexer_sources ADD COLUMN sharing_until_ratio REAL NULL;
        ALTER TABLE indexer_sources ADD COLUMN sharing_stuck_action TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN sharing_stuck_after_days INTEGER NULL;
        """;
}
