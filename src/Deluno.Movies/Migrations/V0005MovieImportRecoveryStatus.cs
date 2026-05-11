using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

public sealed class V0005MovieImportRecoveryStatus : SqliteSqlMigration
{
    public override int Version => 5;

    public override string Name => "movie_import_recovery_status";

    protected override string Sql =>
        """
        ALTER TABLE movie_import_recovery_cases ADD COLUMN status TEXT NOT NULL DEFAULT 'open';
        ALTER TABLE movie_import_recovery_cases ADD COLUMN resolved_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_movie_import_recovery_cases_status_detected
            ON movie_import_recovery_cases (status, detected_utc DESC);
        """;
}
