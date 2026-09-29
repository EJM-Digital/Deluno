using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Auto-tagging rules (#1024): some tags plus some conditions, in the way
/// Sonarr's and Radarr's AutoTagging tables hold them. Both tables start empty,
/// so a new install and every existing install do nothing until a rule is made
/// (#936). A condition of kind "tag" names a tag, so the conditions live in
/// their own rows: a tag rename or delete can then find and follow every one.
/// </summary>
public sealed class V0128AutoTagRules : SqliteSqlMigration
{
    public override int Version => 128;

    public override string Name => "auto_tag_rules";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS auto_tag_rules (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            tags TEXT NOT NULL DEFAULT '',
            remove_automatically INTEGER NOT NULL DEFAULT 0,
            applies_to TEXT NOT NULL DEFAULT 'both',
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS auto_tag_specifications (
            id TEXT PRIMARY KEY,
            rule_id TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            spec_type TEXT NOT NULL,
            negate INTEGER NOT NULL DEFAULT 0,
            value TEXT NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS ix_auto_tag_specifications_rule
            ON auto_tag_specifications (rule_id, sort_order);
        """;
}
