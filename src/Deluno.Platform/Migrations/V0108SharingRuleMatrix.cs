using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A per-category/tracker/tag seeding-rule matrix with Min Seeders and Max
/// Inactive Days guards (#820), matching Cleanuparr's Seeding Rules. Deluno's
/// SharingPolicy was one global object (plus a Strict variant) with no way to
/// vary seeding behaviour by category/tracker/tag, and no guard that could
/// hold a download from removal for having too few seeders or too little
/// recent activity.
///
/// Two tables: <c>sharing_rules</c> holds the matrix itself, evaluated in
/// priority order; <c>sharing_activity_tracking</c> is the small piece of
/// state the Max Inactive Days guard needs that nothing else in Deluno
/// already tracks — how long since a download last had any speed at all,
/// which only persists if something remembers it between passes.
/// </summary>
public sealed class V0108SharingRuleMatrix : SqliteSqlMigration
{
    public override int Version => 108;

    public override string Name => "sharing_rule_matrix";

    protected override string Sql =>
        """
        CREATE TABLE sharing_rules (
            id TEXT PRIMARY KEY,
            priority INTEGER NOT NULL,
            category_pattern TEXT NULL,
            tracker_pattern TEXT NULL,
            tags_csv TEXT NULL,
            tag_match_mode TEXT NOT NULL DEFAULT 'any',
            mode TEXT NULL,
            for_hours INTEGER NULL,
            until_ratio REAL NULL,
            stuck_action TEXT NULL,
            stuck_after_days INTEGER NULL,
            completion_action TEXT NULL,
            min_seeders INTEGER NULL,
            max_inactive_days INTEGER NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        CREATE INDEX idx_sharing_rules_priority ON sharing_rules (priority ASC);

        CREATE TABLE sharing_activity_tracking (
            client_id TEXT NOT NULL,
            queue_item_id TEXT NOT NULL,
            last_active_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (client_id, queue_item_id)
        );
        """;
}
