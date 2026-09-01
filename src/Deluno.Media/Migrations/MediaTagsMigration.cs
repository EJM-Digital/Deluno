using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Media.Migrations;

/// <summary>
/// Adds the catalogue-local side of the platform tag feature. The SQL body is
/// shared by movies and series; only the closed map supplies the database and
/// entry table names.
/// </summary>
public sealed class MediaTagsMigration(MediaKind kind, int version) : SqliteSqlMigration
{
    private readonly MediaTableMap map = MediaTableMap.For(kind);

    public override int Version => version;

    public override string Name => "media_tags";

    protected override string Sql => $"""
        CREATE TABLE IF NOT EXISTS {map.TagTable} (
            {map.TagMediaIdColumn} TEXT NOT NULL,
            tag_id TEXT NOT NULL,
            tag_name TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            PRIMARY KEY ({map.TagMediaIdColumn}, tag_id),
            FOREIGN KEY ({map.TagMediaIdColumn}) REFERENCES {map.EntryTable}(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_{map.TagTable}_tag_name
            ON {map.TagTable} (tag_name COLLATE NOCASE, {map.TagMediaIdColumn});

        -- Older builds temporarily kept title labels in the provider metadata
        -- blob. Preserve those assignments when the real catalogue-local join
        -- is introduced. The legacy id is deterministic and is replaced with
        -- the managed platform tag id the next time the title is saved.
        INSERT OR IGNORE INTO {map.TagTable} ({map.TagMediaIdColumn}, tag_id, tag_name, created_utc)
        SELECT
            entry.id,
            'legacy:' || lower(trim(CAST(value AS TEXT))),
            trim(CAST(value AS TEXT)),
            entry.created_utc
        FROM {map.EntryTable} entry
        CROSS JOIN json_each(
            CASE
                WHEN json_valid(COALESCE(entry.metadata_json, ''))
                 AND json_type(entry.metadata_json, '$.tags') = 'array' THEN
                    json_extract(entry.metadata_json, '$.tags')
                WHEN json_valid(COALESCE(entry.metadata_json, ''))
                 AND json_type(entry.metadata_json, '$.Tags') = 'array' THEN
                    json_extract(entry.metadata_json, '$.Tags')
                WHEN json_valid(COALESCE(entry.metadata_json, ''))
                 AND json_type(entry.metadata_json, '$.tags') = 'text' THEN
                    json_array(json_extract(entry.metadata_json, '$.tags'))
                WHEN json_valid(COALESCE(entry.metadata_json, ''))
                 AND json_type(entry.metadata_json, '$.Tags') = 'text' THEN
                    json_array(json_extract(entry.metadata_json, '$.Tags'))
                ELSE '[]'
            END)
        WHERE trim(CAST(value AS TEXT)) <> '';
        """;
}
