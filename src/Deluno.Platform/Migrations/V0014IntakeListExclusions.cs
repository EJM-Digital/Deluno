using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0014IntakeListExclusions : SqliteSqlMigration
{
    public override int Version => 14;

    public override string Name => "intake_list_exclusions";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS intake_source_exclusions (
            id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL,
            entry_key TEXT NOT NULL,
            title TEXT NOT NULL,
            year INTEGER NULL,
            imdb_id TEXT NULL,
            expires_utc TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (source_id) REFERENCES intake_sources(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_intake_source_exclusions_source_entry
            ON intake_source_exclusions (source_id, entry_key);

        CREATE INDEX IF NOT EXISTS ix_intake_source_exclusions_active
            ON intake_source_exclusions (source_id, expires_utc);
        """;
}
