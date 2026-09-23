using Deluno.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Series.Data;

public sealed class SeriesDispatchRecoveryHandler(ISeriesCatalogRepository catalogRepository)
    : IDispatchRecoveryHandlerComponent
{
    public async Task HandleGrabTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        if (DelunoValueNormalizers.NormalizeMediaType(mediaType) != "tv")
            return;

        var summary = $"Download was sent to {downloadClientName} but never appeared in the client's queue after 2 hours.";
        var recommended = "Check if the download client is running and properly connected. Manual retry may be needed.";
        await catalogRepository.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(title, "grab-timeout", summary, recommended, detailsJson),
            cancellationToken);
    }

    public async Task HandleDetectionTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        if (DelunoValueNormalizers.NormalizeMediaType(mediaType) != "tv")
            return;

        var summary = "The download finished 4 hours ago and has not been imported.";
        var recommended = "The file may have been corrupted, moved, or deleted. Check the download folder and retry if the file is still present.";
        await catalogRepository.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(title, "detection-timeout", summary, recommended, detailsJson),
            cancellationToken);
    }

    public async Task HandleImportTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        if (DelunoValueNormalizers.NormalizeMediaType(mediaType) != "tv")
            return;

        var summary = $"Import was detected but never completed after 24 hours.";
        var recommended = "Check if the import got stuck due to permissions, disk space, or a service crash. Retry the import or verify the file is readable.";
        await catalogRepository.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(title, "import-timeout", summary, recommended, detailsJson),
            cancellationToken);
    }

    public async Task HandleImportFailureAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string importFailureCode,
        string importFailureMessage,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        if (DelunoValueNormalizers.NormalizeMediaType(mediaType) != "tv")
            return;

        var failureReason = !string.IsNullOrWhiteSpace(importFailureMessage)
            ? importFailureMessage
            : importFailureCode != "" ? importFailureCode : "unknown";
        var summary = $"Import failed: {failureReason}";
        var recommended = "Review the failure reason. Common issues: unsupported codec, insufficient permissions, or disk space. Retry after resolving the underlying issue.";
        await catalogRepository.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(title, "import-failed", summary, recommended, detailsJson),
            cancellationToken);
    }

    /// <summary>The download recovered, so the cases written about it close with the reason (#559).</summary>
    public Task HandleDispatchSettledAsync(string dispatchId, string reason, CancellationToken cancellationToken)
        => catalogRepository.SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope.ForDispatch(dispatchId), reason, cancellationToken);

    /// <summary>What a deletion named is gone, so the cases about it close with the reason (#567, #568, #569).</summary>
    public Task<int> HandleScopeSettledAsync(ImportRecoveryCaseScope scope, string reason, CancellationToken cancellationToken)
        => catalogRepository.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken);
}
