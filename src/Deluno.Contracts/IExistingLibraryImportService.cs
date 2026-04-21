namespace Deluno.Contracts;

public interface IExistingLibraryImportService
{
    Task<ExistingLibraryImportResult?> ImportLibraryAsync(string libraryId, CancellationToken cancellationToken);
}
