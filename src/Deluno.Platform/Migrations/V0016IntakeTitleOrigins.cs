using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0016IntakeTitleOrigins : SqliteSqlMigration
{
    public override int Version => 16;

    public override string Name => "intake_title_origins";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS intake_title_origins (
            id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL,
            source_name TEXT NOT NULL,
            provider TEXT NOT NULL,
            media_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            entry_key TEXT NOT NULL,
            title TEXT NOT NULL,
            year INTEGER NULL,
            imdb_id TEXT NULL,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_intake_title_origins_source_entity_entry
            ON intake_title_origins (source_id, media_type, entity_id, entry_key);

        CREATE INDEX IF NOT EXISTS ix_intake_title_origins_title
            ON intake_title_origins (media_type, entity_id, last_seen_utc DESC);
        """;
}
