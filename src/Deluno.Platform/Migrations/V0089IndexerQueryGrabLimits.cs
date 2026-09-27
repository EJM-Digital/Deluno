using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Per-indexer Query Limit and Grab Limit caps, per day or per hour (#750),
/// in addition to the existing request-interval pacing.
/// </summary>
public sealed class V0089IndexerQueryGrabLimits : SqliteSqlMigration
{
    public override int Version => 89;

    public override string Name => "indexer_query_grab_limits";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN query_limit INTEGER NULL;
        ALTER TABLE indexer_sources ADD COLUMN grab_limit INTEGER NULL;
        ALTER TABLE indexer_sources ADD COLUMN limits_unit TEXT NULL;
        """;
}
