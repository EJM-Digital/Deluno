using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Consolidates automated-source exclusions so import lists and collection
/// members have one reviewable history and one restore path. The old list table
/// remains for older callers until the module is fully retired.
/// </summary>
public sealed class V0033UnifiedMediaExclusions : SqliteSqlMigration
{
    public override int Version => 33;

    public override string Name => "unified_media_exclusions";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS media_exclusions (
            id TEXT PRIMARY KEY,
            media_type TEXT NOT NULL,
            source_kind TEXT NOT NULL,
            source_id TEXT NOT NULL,
            source_name TEXT NOT NULL DEFAULT '',
            provider TEXT NOT NULL DEFAULT 'unknown',
            entry_key TEXT NOT NULL,
            title TEXT NOT NULL,
            year INTEGER NULL,
            imdb_id TEXT NULL,
            reason TEXT NOT NULL DEFAULT 'Excluded by user',
            expires_utc TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            UNIQUE (source_kind, source_id, entry_key)
        );

        CREATE INDEX IF NOT EXISTS ix_media_exclusions_active
            ON media_exclusions (media_type, source_kind, source_id, expires_utc);

        INSERT OR IGNORE INTO media_exclusions (
            id, media_type, source_kind, source_id, source_name, provider,
            entry_key, title, year, imdb_id, reason, expires_utc,
            created_utc, updated_utc
        )
        SELECT
            e.id,
            CASE WHEN lower(COALESCE(s.media_type, 'movies')) = 'tv' THEN 'tv' ELSE 'movies' END,
            'import-list',
            e.source_id,
            COALESCE(s.name, ''),
            COALESCE(s.provider, 'unknown'),
            e.entry_key,
            e.title,
            e.year,
            e.imdb_id,
            COALESCE(e.reason, 'Excluded from import list by user'),
            e.expires_utc,
            e.created_utc,
            e.updated_utc
        FROM intake_source_exclusions e
        LEFT JOIN intake_sources s ON s.id = e.source_id;
        """;
}
