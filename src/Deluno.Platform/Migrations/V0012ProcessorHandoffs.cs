using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0012ProcessorHandoffs : SqliteSqlMigration
{
    public override int Version => 12;

    public override string Name => "processor_handoffs";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS processor_handoffs (
            id TEXT PRIMARY KEY,
            library_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            client_id TEXT NOT NULL,
            queue_item_id TEXT NOT NULL,
            release_name TEXT NOT NULL,
            source_path TEXT NOT NULL,
            source_key TEXT NOT NULL,
            processor_name TEXT NULL,
            status TEXT NOT NULL,
            output_path TEXT NULL,
            import_job_id TEXT NULL,
            failure_message TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            UNIQUE(library_id, source_key)
        );

        CREATE INDEX IF NOT EXISTS ix_processor_handoffs_library_updated
            ON processor_handoffs (library_id, updated_utc DESC);

        CREATE INDEX IF NOT EXISTS ix_processor_handoffs_status_updated
            ON processor_handoffs (status, updated_utc DESC);
        """;
}
