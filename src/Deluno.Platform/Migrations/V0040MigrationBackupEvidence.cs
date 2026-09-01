using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Stores the verified backup receipt attached to each migration audit.</summary>
public sealed class V0040MigrationBackupEvidence : SqliteSqlMigration
{
    public override int Version => 40;

    public override string Name => "migration_backup_evidence";

    protected override string Sql =>
        "ALTER TABLE migration_audit_reports ADD COLUMN backup_receipt_json TEXT NULL;";
}
