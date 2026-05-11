namespace Deluno.Realtime;

public interface IRealtimeEventPublisher
{
    Task PublishHealthChangedAsync(
        string source,
        string status,
        string message,
        CancellationToken cancellationToken);

    Task PublishDownloadProgressAsync(
        string id,
        string title,
        double progress,
        double speedMbps,
        string? eta,
        string status,
        CancellationToken cancellationToken);

    Task PublishActivityEventAddedAsync(
        string id,
        string message,
        string category,
        string severity,
        string createdUtc,
        CancellationToken cancellationToken);

    Task PublishQueueItemAddedAsync(
        string id,
        string title,
        string type,
        string status,
        CancellationToken cancellationToken);

    Task PublishQueueItemRemovedAsync(
        string id,
        CancellationToken cancellationToken);

    Task PublishDispatchGrabAttemptAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        string clientName,
        CancellationToken cancellationToken);

    Task PublishDispatchGrabCompletedAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken);

    Task PublishDispatchDetectedAsync(
        string dispatchId,
        string releaseName,
        string? torrentHash,
        long? downloadedBytes,
        CancellationToken cancellationToken);

    Task PublishDispatchImportStartedAsync(
        string dispatchId,
        string releaseName,
        string mediaType,
        CancellationToken cancellationToken);

    Task PublishDispatchImportCompletedAsync(
        string dispatchId,
        string releaseName,
        bool succeeded,
        string? importedPath,
        string? failureReason,
        CancellationToken cancellationToken);
}
