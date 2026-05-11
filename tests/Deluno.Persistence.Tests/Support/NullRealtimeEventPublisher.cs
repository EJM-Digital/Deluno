using Deluno.Realtime;

namespace Deluno.Persistence.Tests.Support;

internal sealed class NullRealtimeEventPublisher : IRealtimeEventPublisher
{
    public Task PublishHealthChangedAsync(
        string source,
        string status,
        string message,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDownloadProgressAsync(
        string id,
        string title,
        double progress,
        double speedMbps,
        string? eta,
        string status,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishActivityEventAddedAsync(
        string id,
        string message,
        string category,
        string severity,
        string createdUtc,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishQueueItemAddedAsync(
        string id,
        string title,
        string type,
        string status,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishQueueItemRemovedAsync(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDispatchGrabAttemptAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        string clientName,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDispatchGrabCompletedAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDispatchDetectedAsync(
        string dispatchId,
        string releaseName,
        string? torrentHash,
        long? downloadedBytes,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDispatchImportStartedAsync(
        string dispatchId,
        string releaseName,
        string mediaType,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PublishDispatchImportCompletedAsync(
        string dispatchId,
        string releaseName,
        bool succeeded,
        string? importedPath,
        string? failureReason,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
