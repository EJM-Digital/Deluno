namespace Deluno.Contracts;

/// <summary>
/// The names of the pushed Live events (#1097). Every one carries its data: a
/// screen applies the payload as it arrives and never fetches after a nudge.
/// They go to the <see cref="LiveRealtimeSubject.Name"/> subject, which a screen
/// joins only while it is on Live or Transfers, so the server knows someone is watching.
/// </summary>
public static class LiveEventNames
{
    public const string DashboardSummaryUpdated = "DashboardSummaryUpdated";
    public const string DownloadSpeedSample = "DownloadSpeedSample";
    public const string JourneyStageChanged = "JourneyStageChanged";
    public const string ProcessorHandoffChanged = "ProcessorHandoffChanged";
    public const string RecoveryCaseChanged = "RecoveryCaseChanged";
    public const string JobChanged = "JobChanged";
    public const string SearchCycleProgress = "SearchCycleProgress";
    public const string IndexerQueryRecorded = "IndexerQueryRecorded";
    public const string ClientStateChanged = "ClientStateChanged";
    public const string DriveSpaceChanged = "DriveSpaceChanged";
}

public static class LiveRealtimeSubject
{
    /// <summary>The subject a screen joins while Live or Transfers is open.</summary>
    public const string Name = "live";
}

/// <summary>Which Dashboard summary sections a change can move; each name is a property of GET /api/dashboard/summary.</summary>
public static class DashboardSectionNames
{
    public const string Library = "library";
    public const string Downloads = "downloads";
    public const string Pipeline = "pipeline";
    public const string Imports = "imports";
    public const string Automation = "automation";
    public const string Indexers = "indexers";
    public const string Processors = "processors";
    public const string Retention = "retention";
    public const string Subtitles = "subtitles";
    public const string Requests = "requests";
    public const string Blocklist = "blocklist";
    public const string Health = "health";
    public const string Storage = "storage";
    public const string System = "system";
    public const string Activity = "activity";

    public static readonly IReadOnlyList<string> All =
    [
        Library, Downloads, Pipeline, Imports, Automation, Indexers, Processors, Retention,
        Subtitles, Requests, Blocklist, Health, Storage, System, Activity
    ];
}

/// <summary>Builds named Dashboard summary sections fresh, so the realtime hub can push them the moment they change.</summary>
public interface IDashboardSectionSource
{
    /// <summary>The built sections by name (as in the summary); a section that failed is left out and named in Unavailable.</summary>
    Task<DashboardSectionBuild> BuildSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken);
}

public sealed record DashboardSectionBuild(
    IReadOnlyDictionary<string, object> Sections,
    IReadOnlyList<string> Unavailable);

/// <summary>How many screens are watching each realtime subject right now.</summary>
public interface IRealtimeWatchers
{
    int Count(string subject);
}

/// <summary>Payload of <c>JourneyStageChanged</c>: one dispatch moved to a new stage on its journey.</summary>
public sealed record JourneyStageChangedEvent(
    string DispatchId,
    string? ReleaseName,
    string? MediaType,
    string? EntityType,
    string? EntityId,
    string? LibraryId,
    string Stage,
    string? Detail,
    string At);

/// <summary>
/// Payload of <c>ProcessorHandoffChanged</c>: a hand-off to the processor moved on. State is the hand-off status
/// (waiting, submitted, accepted, started, completed, failed, rejected, timed-out, stalled, lost, cancelled, released)
/// or, when the processor itself reported a new state, that state (queued, scheduled, working, completed, passed-through, failed).
/// </summary>
public sealed record ProcessorHandoffChangedEvent(
    string HandoffId,
    string? QueueItemId,
    string? LibraryId,
    string? ReleaseName,
    string State,
    string? ProcessorName,
    string? Message,
    string At);

/// <summary>Payload of <c>RecoveryCaseChanged</c>. State: opened or closed.</summary>
public sealed record RecoveryCaseChangedEvent(
    string CaseId,
    string State,
    string? Kind,
    string? Title,
    string? EntityType,
    string? EntityId,
    string At);

/// <summary>Payload of <c>JobChanged</c>. State: started, finished, retrying (failed, will be tried again), failed (given up).</summary>
public sealed record JobChangedEvent(
    string JobId,
    string Kind,
    string State,
    string? Title,
    string? LibraryId,
    string? Message,
    string At);

/// <summary>Payload of <c>SearchCycleProgress</c>. State: running or finished.</summary>
public sealed record SearchCycleProgressEvent(
    string LibraryId,
    string? LibraryName,
    string? MediaType,
    string State,
    int Checked,
    int Planned,
    string At);

/// <summary>Payload of <c>IndexerQueryRecorded</c>: one indexer question, as asked (Phase asked) and as answered (Phase answered).</summary>
public sealed record IndexerQueryRecordedEvent(
    string IndexerId,
    string? IndexerName,
    string Phase,
    string? Query,
    int? ResultCount,
    int? ElapsedMs,
    bool Failed,
    string? Failure,
    string At);

/// <summary>Payload of <c>ClientStateChanged</c>: a download client became reachable, unreachable, paused or resumed.</summary>
public sealed record ClientStateChangedEvent(
    string ClientId,
    string? ClientName,
    bool Reachable,
    bool? Paused,
    string? Message,
    string At);

/// <summary>Payload of <c>DriveSpaceChanged</c>: free space on a drive after an import.</summary>
public sealed record DriveSpaceChangedEvent(
    string Path,
    long FreeBytes,
    long? TotalBytes,
    string At);

/// <summary>Payload of <c>DownloadSpeedSample</c>: the clients combined speed, sent with every reading while someone watches.</summary>
public sealed record DownloadSpeedSampleEvent(
    double DownloadMbps,
    double UploadMbps,
    int Active,
    string At);

public static class LiveEventPublisherExtensions
{
    /// <summary>
    /// Sends a Live push without ever letting it undo or fail the change it reports:
    /// the data is already saved, and a missed push is caught up by the next one or a reconnect.
    /// </summary>
    public static async Task PublishSafelyAsync(this IRealtimeEventPublisher? publisher, Func<IRealtimeEventPublisher, Task> publish)
    {
        if (publisher is null)
        {
            return;
        }

        try
        {
            await publish(publisher);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    public static Task PublishJourneyStageChangedAsync(this IRealtimeEventPublisher publisher, JourneyStageChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.JourneyStageChanged, payload, cancellationToken);

    public static Task PublishProcessorHandoffChangedAsync(this IRealtimeEventPublisher publisher, ProcessorHandoffChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.ProcessorHandoffChanged, payload, cancellationToken);

    public static Task PublishRecoveryCaseChangedAsync(this IRealtimeEventPublisher publisher, RecoveryCaseChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.RecoveryCaseChanged, payload, cancellationToken);

    public static Task PublishJobChangedAsync(this IRealtimeEventPublisher publisher, JobChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.JobChanged, payload, cancellationToken);

    public static Task PublishSearchCycleProgressAsync(this IRealtimeEventPublisher publisher, SearchCycleProgressEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.SearchCycleProgress, payload, cancellationToken);

    public static Task PublishIndexerQueryRecordedAsync(this IRealtimeEventPublisher publisher, IndexerQueryRecordedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.IndexerQueryRecorded, payload, cancellationToken);

    public static Task PublishClientStateChangedAsync(this IRealtimeEventPublisher publisher, ClientStateChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.ClientStateChanged, payload, cancellationToken);

    public static Task PublishDriveSpaceChangedAsync(this IRealtimeEventPublisher publisher, DriveSpaceChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.DriveSpaceChanged, payload, cancellationToken);

    public static Task PublishDownloadSpeedSampleAsync(this IRealtimeEventPublisher publisher, DownloadSpeedSampleEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.DownloadSpeedSample, payload, cancellationToken);
}
