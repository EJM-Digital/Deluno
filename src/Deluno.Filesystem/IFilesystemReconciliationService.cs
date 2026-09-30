namespace Deluno.Filesystem;

public interface IFilesystemReconciliationService
{
    Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken);

    /// <summary>One film's or show's own rescan; null when the title does not exist (#997).</summary>
    Task<TitleRescanResult?> RescanTitleAsync(string mediaType, string titleId, CancellationToken cancellationToken);

    Task<FilesystemReconciliationRepairResult> RepairAsync(
        FilesystemReconciliationRepairRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// "These files really are gone": works out which tracked files are missing
    /// right now (each re-checked), marks them a chunk at a time and clears the
    /// note. Null when the library has no such note (#1070).
    /// </summary>
    Task<LibraryFilesGoneResult?> ConfirmFilesGoneAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>How many missing files the library's unreachable note counts; null when there is no note.</summary>
    Task<int?> GetUnreachableNoticeMissingCountAsync(string libraryId, CancellationToken cancellationToken);
}
