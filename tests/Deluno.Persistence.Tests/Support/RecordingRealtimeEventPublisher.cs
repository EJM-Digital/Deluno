using System.Collections.Concurrent;

namespace Deluno.Persistence.Tests.Support;

internal sealed class RecordingRealtimeEventPublisher : NullRealtimeEventPublisher, Deluno.Contracts.IRealtimeEventPublisher
{
    private readonly ConcurrentQueue<(string Name, object Payload)> liveEvents = new();

    /// <summary>Every pushed Live event (#1097), in the order it was published.</summary>
    public IReadOnlyList<(string Name, object Payload)> LiveEvents => liveEvents.ToArray();

    public IReadOnlyList<T> LiveEventsOf<T>(string name) where T : class
        => liveEvents.Where(item => item.Name == name).Select(item => (T)item.Payload).ToArray();

    private readonly ConcurrentQueue<(string DispatchId, bool Succeeded, string? FailureReason)> importCompletions = new();

    /// <summary>Every dispatch import-completed push, in order.</summary>
    public IReadOnlyList<(string DispatchId, bool Succeeded, string? FailureReason)> ImportCompletions => importCompletions.ToArray();

    public override Task PublishDispatchImportCompletedAsync(string dispatchId, string releaseName, bool succeeded, string? importedPath, string? failureReason, CancellationToken cancellationToken)
    {
        importCompletions.Enqueue((dispatchId, succeeded, failureReason));
        return Task.CompletedTask;
    }

    private readonly ConcurrentQueue<(string JobId, string State)> importStates = new();

    /// <summary>Every import-state push (#1209), in order.</summary>
    public IReadOnlyList<(string JobId, string State)> ImportStates => importStates.ToArray();

    public override Task PublishImportStateChangedAsync(string jobId, string state, string? entityType, string? entityId, string? title, string? errorMessage, string changedUtc, CancellationToken cancellationToken)
    {
        importStates.Enqueue((jobId, state));
        return Task.CompletedTask;
    }

    private readonly ConcurrentQueue<string> downloadProgressIds = new();

    public IReadOnlyList<string> DownloadProgressIds => downloadProgressIds.ToArray();

    public override Task PublishDownloadProgressAsync(
        string id,
        string title,
        double progress,
        double speedMbps,
        string? eta,
        string status,
        CancellationToken cancellationToken)
    {
        downloadProgressIds.Enqueue(id);
        return Task.CompletedTask;
    }

    private readonly ConcurrentQueue<(string LibraryId, string LibraryName, string MediaType, int Planned, int Queued, int Skipped)> searchRunsCompleted = new();

    /// <summary>Every finished search run announced to the Dashboard and Library groups, in order.</summary>
    public IReadOnlyList<(string LibraryId, string LibraryName, string MediaType, int Planned, int Queued, int Skipped)> SearchRunsCompleted => searchRunsCompleted.ToArray();

    public new Task PublishSearchRunCompletedAsync(string libraryId, string libraryName, string mediaType, int plannedCount, int queuedCount, int skippedCount, string completedUtc, CancellationToken cancellationToken)
    {
        searchRunsCompleted.Enqueue((libraryId, libraryName, mediaType, plannedCount, queuedCount, skippedCount));
        return Task.CompletedTask;
    }

    public Task PublishLiveEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        liveEvents.Enqueue((eventName, payload));
        return Task.CompletedTask;
    }

    private readonly ConcurrentQueue<(string Type, string Id)> published = new();

    private readonly ConcurrentQueue<(string Name, object Payload)> dashboardEvents = new();

    /// <summary>Every pushed event on the dashboard subject (the System overview, #1098), in order.</summary>
    public IReadOnlyList<(string Name, object Payload)> DashboardEvents => dashboardEvents.ToArray();

    public Task PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        dashboardEvents.Enqueue((eventName, payload));
        return Task.CompletedTask;
    }

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
