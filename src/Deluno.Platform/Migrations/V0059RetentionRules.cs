using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Retention rules and what they did (#494).
///
/// <para><b>Rules are data.</b> A rule's conditions are one JSON list of
/// <c>field:operator:value</c> strings, not a column per criterion. Weir's
/// Pruner gave each criterion its own column, module and migration, and that is
/// why it never grew: every new criterion was a development task.</para>
///
/// <para><b>Every run is kept, with every title it considered.</b> A preview is
/// what a person is shown before anything is removed; a cleanup names the
/// preview it acted on, and each title records whether it was removed, why it
/// was left alone, or what went wrong.</para>
/// </summary>
public sealed class V0059RetentionRules : SqliteSqlMigration
{
    public override int Version => 59;

    public override string Name => "retention_rules";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS retention_rules (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            conditions_json TEXT NOT NULL DEFAULT '[]',
            media_server_ids_json TEXT NOT NULL DEFAULT '[]',
            remove_from_servers INTEGER NOT NULL DEFAULT 1,
            schedule_mode TEXT NOT NULL DEFAULT 'manual',
            schedule_interval_hours INTEGER NOT NULL DEFAULT 24,
            last_scheduled_run_utc TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS retention_runs (
            id TEXT PRIMARY KEY,
            rule_id TEXT NOT NULL,
            rule_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            trigger TEXT NOT NULL,
            preview_run_id TEXT NULL,
            started_utc TEXT NOT NULL,
            finished_utc TEXT NULL,
            matched_count INTEGER NOT NULL DEFAULT 0,
            excluded_count INTEGER NOT NULL DEFAULT 0,
            removed_count INTEGER NOT NULL DEFAULT 0,
            failed_count INTEGER NOT NULL DEFAULT 0,
            bytes INTEGER NOT NULL DEFAULT 0,
            summary TEXT NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS ix_retention_runs_rule_started
            ON retention_runs (rule_id, started_utc DESC);

        CREATE TABLE IF NOT EXISTS retention_run_items (
            run_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            media_type TEXT NOT NULL,
            title TEXT NOT NULL,
            year INTEGER NULL,
            size_bytes INTEGER NULL,
            reasons_json TEXT NOT NULL DEFAULT '[]',
            outcome TEXT NOT NULL,
            message TEXT NULL,
            PRIMARY KEY (run_id, entity_id)
        );
        """;
}
