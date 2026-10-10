using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1208: the hand-off table keeps one row per source path, and a release sent again gives that row a
/// new id. The processor still holds the old id and reports against it, so the old id is kept here,
/// pointing at the id that replaced it, and a late report for it is settled instead of failing.
/// </summary>
public sealed class V0138ProcessorHandoffSupersededIds : SqliteSqlMigration
{
    public override int Version => 138;

    public override string Name => "processor_handoff_superseded_ids";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS processor_handoff_superseded (
            id TEXT NOT NULL PRIMARY KEY,
            library_id TEXT NOT NULL,
            source_key TEXT NOT NULL,
            release_name TEXT NOT NULL,
            last_status TEXT NOT NULL,
            superseded_by_id TEXT NOT NULL,
            superseded_utc TEXT NOT NULL,
            settled_utc TEXT NULL,
            settled_report_status TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_processor_handoff_superseded_library
            ON processor_handoff_superseded (library_id, superseded_utc DESC);
        """;
}
