using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0019LibraryImportRuns : SqliteSqlMigration
{
    public override int Version => 19;

    public override string Name => "library_import_runs";

    /// <remarks>
    /// The partial unique index is the whole point of the design: one active
    /// run per library, enforced by the database rather than by a check that
    /// two concurrent requests could both pass.
    /// </remarks>
    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS library_import_runs (
            id TEXT PRIMARY KEY,
            library_id TEXT NOT NULL,
            library_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            root_path TEXT NOT NULL,
            status TEXT NOT NULL,
            estimated_total INTEGER NOT NULL DEFAULT 0,
            processed_count INTEGER NOT NULL DEFAULT 0,
            imported_count INTEGER NOT NULL DEFAULT 0,
            skipped_count INTEGER NOT NULL DEFAULT 0,
            deferred_count INTEGER NOT NULL DEFAULT 0,
            cursor_key TEXT NULL,
            sample_titles TEXT NULL,
            last_error TEXT NULL,
            created_utc TEXT NOT NULL,
            started_utc TEXT NULL,
            updated_utc TEXT NOT NULL,
            completed_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_library_import_runs_library
            ON library_import_runs (library_id, created_utc DESC);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_library_import_runs_active
            ON library_import_runs (library_id)
            WHERE status IN ('queued', 'running', 'paused');

        CREATE TABLE IF NOT EXISTS library_import_issues (
            id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            library_id TEXT NOT NULL,
            source_path TEXT NOT NULL,
            kind TEXT NOT NULL,
            detail TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_library_import_issues_run
            ON library_import_issues (run_id, created_utc);

        -- A slice that dies after recording an issue but before committing its
        -- position replays those entries. The issue is the same issue, so it
        -- must not be recorded twice.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_library_import_issues_entry
            ON library_import_issues (run_id, source_path, kind);
        """;
}
