using System.Collections.Concurrent;

namespace Deluno.Persistence.Tests.Support;

internal sealed class RecordingRealtimeEventPublisher : NullRealtimeEventPublisher
{
    private readonly ConcurrentQueue<(string Type, string Id)> published = new();

    public IReadOnlyList<(string Type, string Id)> Published => published.ToArray();

    public override Task PublishEntityChangedAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        published.Enqueue((entityType, entityId));
        return Task.CompletedTask;
    }
}
