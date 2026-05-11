using Deluno.Jobs.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Series.Data;

public sealed class SeriesDispatchRecoveryHandler(ISeriesCatalogRepository catalogRepository)
    : IDispatchRecoveryHandler
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
        if (mediaType != "tv")
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
        if (mediaType != "tv")
            return;

        var summary = $"Download was detected but import was not attempted after 4 hours.";
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
        if (mediaType != "tv")
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
        if (mediaType != "tv")
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
}
