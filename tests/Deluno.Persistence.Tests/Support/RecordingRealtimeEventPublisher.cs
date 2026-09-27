using System.Collections.Concurrent;

namespace Deluno.Persistence.Tests.Support;

internal sealed class RecordingRealtimeEventPublisher : NullRealtimeEventPublisher
{
    private readonly ConcurrentQueue<(string Type, string Id)> published = new();
    private readonly ConcurrentQueue<(string DispatchId, string ReleaseName, string MediaType)> dispatchImportsStarted = new();
    private readonly ConcurrentQueue<(string Category, string Severity)> activityEvents = new();

    public IReadOnlyList<(string Type, string Id)> Published => published.ToArray();
    public IReadOnlyList<(string DispatchId, string ReleaseName, string MediaType)> DispatchImportsStarted => dispatchImportsStarted.ToArray();
    public IReadOnlyList<(string Category, string Severity)> ActivityEvents => activityEvents.ToArray();

    public override Task PublishActivityEventAddedAsync(
        string id,
        string message,
        string category,
        string severity,
        string createdUtc,
        CancellationToken cancellationToken)
    {
        activityEvents.Enqueue((category, severity));
        return Task.CompletedTask;
    }

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
