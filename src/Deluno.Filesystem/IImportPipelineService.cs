namespace Deluno.Filesystem;

public interface IImportPipelineService
{
    Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request, CancellationToken cancellationToken);

    Task<ImportPipelineResult> ExecuteAsync(ImportExecuteRequest request, CancellationToken cancellationToken);
}

public sealed record ImportPipelineResult(
    bool Succeeded,
    int StatusCode,
    ImportExecuteResponse? Response,
    string Message);
