using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IMigrationAuditRepository
{
    Task<MigrationAuditReport> RecordMigrationAuditReportAsync(
        MigrationAuditReport report,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MigrationAuditReport>> ListMigrationAuditReportsAsync(
        int take,
        CancellationToken cancellationToken);

    Task<MigrationAuditReport?> GetMigrationAuditReportAsync(
        string id,
        CancellationToken cancellationToken);
}
