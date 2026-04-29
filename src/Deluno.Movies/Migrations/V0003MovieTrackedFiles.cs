using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

public sealed class V0003MovieTrackedFiles : SqliteSqlMigration
{
    public override int Version => 3;

    public override string Name => "movie_tracked_files";

    protected override string Sql =>
        """
        ALTER TABLE movie_wanted_state ADD COLUMN file_path TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN file_size_bytes INTEGER NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN imported_utc TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN last_verified_utc TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN missing_detected_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_movie_wanted_state_file_path
            ON movie_wanted_state (file_path)
            WHERE file_path IS NOT NULL;
        """;
}
