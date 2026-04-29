namespace Deluno.Filesystem;

public interface IFilesystemReconciliationService
{
    Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken);

    Task<FilesystemReconciliationRepairResult> RepairAsync(
        FilesystemReconciliationRepairRequest request,
        CancellationToken cancellationToken);
}
