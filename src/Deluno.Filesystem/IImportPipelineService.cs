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
    string Message,
    /// <summary>
    /// The machine-readable reason a failed import was refused, matching
    /// <c>ImportFailurePolicy</c>'s failure kinds (e.g. <c>"replacementRejected"</c>).
    /// Null for a success, and for a failure that carries no such kind.
    /// </summary>
    string? FailureKind = null);
