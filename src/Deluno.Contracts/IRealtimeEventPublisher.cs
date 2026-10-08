namespace Deluno.Contracts;

/// <summary>
/// Low-level event contract used by durable state and application modules to
/// publish changes without depending on a transport implementation.
/// </summary>
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

    Task PublishQueueItemStatusChangedAsync(
        string id,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken);

    Task PublishSearchRunCompletedAsync(
        string libraryId,
        string libraryName,
        string mediaType,
        int plannedCount,
        int queuedCount,
        int skippedCount,
        string completedUtc,
        CancellationToken cancellationToken);

    Task PublishImportStateChangedAsync(
        string jobId,
        string state,
        string? entityType,
        string? entityId,
        string? title,
        string? errorMessage,
        string changedUtc,
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

    /// <summary>
    /// The generic entity-change family from ADR-002: identity, not the new
    /// value, so the client invalidates <paramref name="entityType"/> plus
    /// <paramref name="entityId"/> and refetches rather than trusting a
    /// second serialization of the object over the wire. The event name on
    /// the wire is "{entityType}Changed" (for example, "QualityProfileChanged").
    /// </summary>
    Task PublishEntityChangedAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A film or show changed, from whatever wrote it (#1179). Sent to the Dashboard, to the
    /// title's own subject (so its page hears about it cheaply) and to every library
    /// it is in. The default is the plain entity-change event, so a test double needs no change.
    /// </summary>
    Task PublishTitleChangedAsync(
        string entityType,
        string entityId,
        IReadOnlyCollection<string> libraryIds,
        CancellationToken cancellationToken)
        => PublishEntityChangedAsync(entityType, entityId, cancellationToken);

    /// <summary>
    /// A pushed event that carries its own data, on the "dashboard" subject (the System screen, #1098).
    /// The default does nothing, so a test double that does not care needs no change.
    /// </summary>
    Task PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// A pushed Live event (#1097) that carries its own data, on the "live" subject.
    /// The default does nothing, so a test double that does not care needs no change.
    /// </summary>
    Task PublishLiveEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
