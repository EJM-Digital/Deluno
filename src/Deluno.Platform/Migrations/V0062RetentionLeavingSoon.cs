using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Leaving soon (#494, DESIGN-008): a match waits before it goes, somebody can
/// keep it, and a season or episode removed by a rule says so.
///
/// <list type="bullet">
/// <item><c>retention_marks</c> - a title a rule has marked Leaving soon, why,
/// and the day it goes. One row per rule, so a title two rules want carries both
/// reasons, and goes on the first date.</item>
/// <item><c>retention_keeps</c> - a title somebody kept, from one rule or all of
/// them, with their note.</item>
/// <item><c>retention_episode_removals</c> - an episode a rule removed while
/// its show stayed: the reason it is no longer wanted, and the way back.</item>
/// </list>
///
/// <para>A rule's schedule becomes a check every few hours, and each rule says
/// how long a match waits and whether it is removed on the day or waits for a
/// person to confirm. The preview and cleanup schedule columns are no longer
/// read.</para>
/// </summary>
public sealed class V0062RetentionLeavingSoon : SqliteSqlMigration
{
    public override int Version => 62;

    public override string Name => "retention_leaving_soon";

    protected override string Sql =>
        """
        ALTER TABLE retention_rules ADD COLUMN wait_days INTEGER NOT NULL DEFAULT 14;
        ALTER TABLE retention_rules ADD COLUMN removal_mode TEXT NOT NULL DEFAULT 'automatic';
        ALTER TABLE retention_rules ADD COLUMN check_interval_hours INTEGER NOT NULL DEFAULT 8;
        ALTER TABLE retention_rules ADD COLUMN last_checked_utc TEXT NULL;

        CREATE TABLE IF NOT EXISTS retention_marks (
            rule_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            level TEXT NOT NULL,
            show_id TEXT NULL,
            season_number INTEGER NULL,
            episode_ids_json TEXT NOT NULL DEFAULT '[]',
            title TEXT NOT NULL,
            year INTEGER NULL,
            size_bytes INTEGER NULL,
            reasons_json TEXT NOT NULL DEFAULT '[]',
            first_matched_utc TEXT NOT NULL,
            remove_after_utc TEXT NOT NULL,
            state TEXT NOT NULL,
            state_reason TEXT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (rule_id, entity_id)
        );

        CREATE INDEX IF NOT EXISTS ix_retention_marks_entity ON retention_marks (entity_id);
        CREATE INDEX IF NOT EXISTS ix_retention_marks_show ON retention_marks (show_id);

        CREATE TABLE IF NOT EXISTS retention_keeps (
            id TEXT PRIMARY KEY,
            entity_id TEXT NOT NULL,
            level TEXT NOT NULL,
            title TEXT NOT NULL,
            rule_id TEXT NULL,
            note TEXT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_retention_keeps_entity_rule ON retention_keeps (entity_id, IFNULL(rule_id, ''));

        CREATE TABLE IF NOT EXISTS retention_episode_removals (
            episode_id TEXT PRIMARY KEY,
            series_id TEXT NOT NULL,
            season_number INTEGER NULL,
            episode_number INTEGER NULL,
            rule_id TEXT NOT NULL,
            rule_name TEXT NOT NULL,
            reason TEXT NOT NULL,
            removed_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_retention_episode_removals_series ON retention_episode_removals (series_id);
        """;
}
