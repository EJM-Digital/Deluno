using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Retains the typed integration failure behind each indexer query event.
/// ErrorMessage remains a bounded display field; the JSON preserves the
/// stable service, operation, kind, retry, and upstream-detail vocabulary for
/// diagnostics after the original request has completed.
/// </summary>
public sealed class V0021IndexerQueryFailureDetails : SqliteSqlMigration
{
    public override int Version => 21;

    public override string Name => "indexer_query_failure_details";

    protected override string Sql =>
        "ALTER TABLE indexer_query_events ADD COLUMN failure_json TEXT NULL;";
}
