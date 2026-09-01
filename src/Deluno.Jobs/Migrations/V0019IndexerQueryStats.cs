using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Stores bounded, credential-free telemetry for each outbound indexer query.
/// The row is intentionally separate from title search history: one title
/// search fans out to several indexers, and the scoreboard needs to see every
/// answer, including failures and throttled requests.
/// </summary>
public sealed class V0019IndexerQueryStats : SqliteSqlMigration
{
    public override int Version => 19;

    public override string Name => "indexer_query_stats";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS indexer_query_events (
            id TEXT PRIMARY KEY,
            indexer_id TEXT NOT NULL,
            indexer_name TEXT NOT NULL,
            query_text TEXT NOT NULL,
            categories TEXT NOT NULL,
            media_type TEXT NOT NULL,
            query_kind TEXT NOT NULL,
            outcome TEXT NOT NULL,
            elapsed_ms INTEGER NOT NULL,
            candidate_count INTEGER NOT NULL DEFAULT 0,
            error_message TEXT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_indexer_query_events_created
            ON indexer_query_events (created_utc DESC);

        CREATE INDEX IF NOT EXISTS ix_indexer_query_events_indexer_created
            ON indexer_query_events (indexer_id, created_utc DESC);
        """;
}
