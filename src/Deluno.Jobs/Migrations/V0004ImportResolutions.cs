using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0004ImportResolutions : SqliteSqlMigration
{
    public override int Version => 4;

    public override string Name => "import_resolutions";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS import_resolutions (
            id TEXT PRIMARY KEY,
            dispatch_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            catalog_id TEXT NOT NULL,
            catalog_item_type TEXT NOT NULL,
            import_attempt_utc TEXT NOT NULL,
            import_success_utc TEXT NULL,
            import_failure_utc TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            created_utc TEXT NOT NULL,
            FOREIGN KEY (dispatch_id) REFERENCES download_dispatches (id)
        );

        CREATE INDEX IF NOT EXISTS ix_import_resolutions_dispatch
            ON import_resolutions (dispatch_id);

        CREATE INDEX IF NOT EXISTS ix_import_resolutions_catalog
            ON import_resolutions (media_type, catalog_id, catalog_item_type);

        CREATE INDEX IF NOT EXISTS ix_import_resolutions_success
            ON import_resolutions (import_success_utc DESC)
            WHERE import_success_utc IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_import_resolutions_failure
            ON import_resolutions (import_failure_utc DESC)
            WHERE import_failure_utc IS NOT NULL;
        """;
}
