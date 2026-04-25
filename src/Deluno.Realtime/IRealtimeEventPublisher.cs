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
}
