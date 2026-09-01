using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores acquisition controls that used to be hidden behind a single
/// enabled/disabled switch. The release profiles are platform-owned because
/// the same tag vocabulary and rule can govern both catalogue engines.
/// </summary>
public sealed class V0032AcquisitionPolicy : SqliteSqlMigration
{
    public override int Version => 32;

    public override string Name => "acquisition_policy";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN minimum_age_minutes INTEGER NULL;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN retention_days INTEGER NULL;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN maximum_size_mb INTEGER NULL;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN prefer_indexer_flags TEXT NULL;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN availability_delay_days INTEGER NULL;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN rss_enabled INTEGER NOT NULL DEFAULT 1;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN automatic_search_enabled INTEGER NOT NULL DEFAULT 1;\n"
        + "ALTER TABLE indexer_sources ADD COLUMN interactive_search_enabled INTEGER NOT NULL DEFAULT 1;\n"
        + "CREATE TABLE IF NOT EXISTS release_profiles (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    name TEXT NOT NULL,\n"
        + "    tag_name TEXT NOT NULL DEFAULT '',\n"
        + "    preferred_protocol TEXT NOT NULL DEFAULT 'any',\n"
        + "    usenet_delay_minutes INTEGER NOT NULL DEFAULT 0,\n"
        + "    torrent_delay_minutes INTEGER NOT NULL DEFAULT 0,\n"
        + "    must_contain TEXT NOT NULL DEFAULT '',\n"
        + "    must_not_contain TEXT NOT NULL DEFAULT '',\n"
        + "    preferred_terms_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE UNIQUE INDEX IF NOT EXISTS ix_release_profiles_tag_name\n"
        + "    ON release_profiles (lower(trim(tag_name)));\n"
        + "CREATE INDEX IF NOT EXISTS ix_indexer_sources_search_kinds\n"
        + "    ON indexer_sources (is_enabled, automatic_search_enabled, interactive_search_enabled, rss_enabled);";
}
