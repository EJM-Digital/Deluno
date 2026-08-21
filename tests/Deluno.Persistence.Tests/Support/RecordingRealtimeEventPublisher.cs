using System.Collections.Concurrent;

namespace Deluno.Persistence.Tests.Support;

internal sealed class RecordingRealtimeEventPublisher : NullRealtimeEventPublisher
{
    private readonly ConcurrentQueue<(string Type, string Id)> published = new();
    private readonly ConcurrentQueue<(string DispatchId, string ReleaseName, string MediaType)> dispatchImportsStarted = new();

    public IReadOnlyList<(string Type, string Id)> Published => published.ToArray();
    public IReadOnlyList<(string DispatchId, string ReleaseName, string MediaType)> DispatchImportsStarted => dispatchImportsStarted.ToArray();

    public override Task PublishEntityChangedAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        published.Enqueue((entityType, entityId));
        return Task.CompletedTask;
    }

    public override Task PublishDispatchImportStartedAsync(
        string dispatchId,
        string releaseName,
        string mediaType,
        CancellationToken cancellationToken)
    {
        dispatchImportsStarted.Enqueue((dispatchId, releaseName, mediaType));
        return Task.CompletedTask;
    }
}
