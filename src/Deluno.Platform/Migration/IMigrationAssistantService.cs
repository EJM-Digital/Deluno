using Deluno.Platform.Contracts;

namespace Deluno.Platform.Migration;

public interface IMigrationAssistantService
{
    Task<MigrationReport> PreviewAsync(MigrationImportRequest request, CancellationToken cancellationToken);

    Task<MigrationApplyResponse> ApplyAsync(MigrationImportRequest request, CancellationToken cancellationToken);
}
