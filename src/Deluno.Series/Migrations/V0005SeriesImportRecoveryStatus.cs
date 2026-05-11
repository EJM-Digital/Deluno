using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

public sealed class V0005SeriesImportRecoveryStatus : SqliteSqlMigration
{
    public override int Version => 5;

    public override string Name => "series_import_recovery_status";

    protected override string Sql =>
        """
        ALTER TABLE series_import_recovery_cases ADD COLUMN status TEXT NOT NULL DEFAULT 'open';
        ALTER TABLE series_import_recovery_cases ADD COLUMN resolved_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_series_import_recovery_cases_status_detected
            ON series_import_recovery_cases (status, detected_utc DESC);
        """;
}
