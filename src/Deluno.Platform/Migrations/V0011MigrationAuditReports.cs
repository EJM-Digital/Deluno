using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0011MigrationAuditReports : SqliteSqlMigration
{
    public override int Version => 11;

    public override string Name => "migration_audit_reports";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS migration_audit_reports (
            id TEXT PRIMARY KEY,
            source_kind TEXT NOT NULL,
            source_name TEXT NOT NULL,
            applied_utc TEXT NOT NULL,
            preflight_report_json TEXT NOT NULL,
            result_report_json TEXT NOT NULL,
            applied_items_json TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_migration_audit_reports_applied_utc
            ON migration_audit_reports (applied_utc DESC);
        """;
}
