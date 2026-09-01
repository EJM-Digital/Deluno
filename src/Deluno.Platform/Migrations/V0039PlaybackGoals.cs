using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Persists device capabilities and owner-selected playback goals.</summary>
public sealed class V0039PlaybackGoals : SqliteSqlMigration
{
    public override int Version => 39;

    public override string Name => "playback_goals";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS playback_device_profiles (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    name TEXT NOT NULL,\n"
        + "    capabilities_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    is_enabled INTEGER NOT NULL DEFAULT 1,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE TABLE IF NOT EXISTS playback_device_groups (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    name TEXT NOT NULL,\n"
        + "    mode TEXT NOT NULL DEFAULT 'every-device',\n"
        + "    device_profile_ids_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    primary_device_profile_id TEXT NULL,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE TABLE IF NOT EXISTS playback_goals (\n"
        + "    id TEXT PRIMARY KEY,\n"
        + "    name TEXT NOT NULL,\n"
        + "    media_type TEXT NOT NULL,\n"
        + "    device_group_id TEXT NOT NULL,\n"
        + "    must_play INTEGER NOT NULL DEFAULT 1,\n"
        + "    required_trait_ids_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    required_any_trait_groups_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    preferred_trait_ids_json TEXT NOT NULL DEFAULT '[]',\n"
        + "    stop_when_trait_id TEXT NULL,\n"
        + "    created_utc TEXT NOT NULL,\n"
        + "    updated_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_playback_goals_media_type\n"
        + "    ON playback_goals (media_type, name);\n";
}
