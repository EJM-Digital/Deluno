namespace Deluno.Filesystem;

public interface IFilesystemReconciliationService
{
    Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken);

    /// <summary>One film's or show's own rescan; null when the title does not exist (#997).</summary>
    Task<TitleRescanResult?> RescanTitleAsync(string mediaType, string titleId, CancellationToken cancellationToken);

    Task<FilesystemReconciliationRepairResult> RepairAsync(
        FilesystemReconciliationRepairRequest request,
        CancellationToken cancellationToken);
}
