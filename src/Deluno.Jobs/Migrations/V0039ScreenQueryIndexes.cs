using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1081: the Jobs, Downloads, Activity, Dashboard and Indexers screens each
/// asked for a newest-first list or a windowed count that no index covered, so
/// every request scanned and sorted the whole table. These indexes match the
/// exact ORDER BY / GROUP BY of those queries so the plan reads only the rows it
/// returns. Nothing about the data changes.
/// </summary>
public sealed class V0039ScreenQueryIndexes : SqliteSqlMigration
{
    public override int Version => 39;

    public override string Name => "screen_query_indexes";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_job_queue_created_id
            ON job_queue (created_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_job_queue_status_created
            ON job_queue (status, created_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_job_queue_status_completed
            ON job_queue (status, completed_utc);

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_sort
            ON download_dispatches (COALESCE(grab_attempted_utc, created_utc) DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_created_covering
            ON download_dispatches (created_utc, status, indexer_id, indexer_name, grab_status);

        CREATE INDEX IF NOT EXISTS ix_activity_events_created_id
            ON activity_events (created_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_activity_events_entity_type_created
            ON activity_events (related_entity_type, created_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_activity_events_entity_created
            ON activity_events (related_entity_type, related_entity_id, created_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_indexer_query_events_covering
            ON indexer_query_events (created_utc, indexer_id, query_kind, outcome, elapsed_ms, candidate_count);

        CREATE INDEX IF NOT EXISTS ix_indexer_query_events_indexer_name
            ON indexer_query_events (indexer_id, created_utc DESC, indexer_name);
        """;
}
