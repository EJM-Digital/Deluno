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
    /// <summary>The titles Deluno is searching for right now changed (a search began, moved on or ended): the whole list, so a screen replaces its own.</summary>
    public const string SearchesInFlightChanged = "SearchesInFlightChanged";
    public const string IndexerQueryRecorded = "IndexerQueryRecorded";
    public const string ClientStateChanged = "ClientStateChanged";
    public const string DriveSpaceChanged = "DriveSpaceChanged";
    public const string ReleasesTurnedDown = "ReleasesTurnedDown";
    public const string ConnectionUsed = "ConnectionUsed";
    public const string ConnectionChecked = "ConnectionChecked";
    /// <summary>Something that counts in the bell changed (a recovery case, a hand-off, a client, a leftover folder, a wanted title with no release). Sent on the dashboard subject, so the bell needs no screen open.</summary>
    public const string AttentionChanged = "AttentionChanged";
    /// <summary>The internet went down or came back: sent on the dashboard subject, so the outage banner needs no poll.</summary>
    public const string ConnectivityChanged = "ConnectivityChanged";
    /// <summary>A download-health record was written or cleared (a strike, a finding ending, an ignore).</summary>
    public const string DownloadHealthChanged = "DownloadHealthChanged";
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

    /// <summary>How many browsers are connected for live updates, whatever they have joined. Zero for a source that cannot tell.</summary>
    int ConnectionCount => 0;
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

/// <summary>Payload of <c>JobChanged</c>. State: queued (added), started, waiting (handed back to wait its turn), finished, retrying (failed, will be tried again), failed (given up).</summary>
public sealed record JobChangedEvent(
    string JobId,
    string Kind,
    string State,
    string? Title,
    string? LibraryId,
    string? Message,
    string At);

/// <summary>One title Deluno is searching for right now (#1442): who started it and what it is doing, in plain words.</summary>
public sealed record SearchingTitle(string EntityType, string EntityId, string Title, string StartedBy, string Label, string StartedUtc);

/// <summary>Payload of <c>SearchesInFlightChanged</c>: every title being searched for now. A title missing from the list is no longer being searched.</summary>
public sealed record SearchesInFlightChangedEvent(IReadOnlyList<SearchingTitle> Searches, string At);

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

/// <summary>One release a search turned down: its name, the plain group it falls in and that group's label (#1098).</summary>
public sealed record TurnedDownItemEvent(string Release, string Group, string Label, bool NewToday = true);

/// <summary>Payload of <c>ReleasesTurnedDown</c>: one search looked at releases and did not take these. Sent once per search with any.</summary>
public sealed record ReleasesTurnedDownEvent(
    string? Title,
    string? EntityType,
    string? EntityId,
    string AtUtc,
    IReadOnlyList<TurnedDownItemEvent> Items);

/// <summary>
/// What kinds of connection <c>ConnectionUsed</c> and the Live connections list speak of.
/// </summary>
public static class ConnectionKinds
{
    public const string Processor = "processor";
    public const string MediaServer = "mediaServer";
    public const string Requests = "requests";
    public const string Subtitles = "subtitles";
    public const string Lists = "lists";
    public const string Indexer = "indexer";
    public const string DownloadClient = "downloadClient";
    public const string Metadata = "metadata";
}

/// <summary>
/// Payload of <c>ConnectionUsed</c>: Deluno just used a connection (handed a title to a processor, sent a media
/// server refresh, received a Seerr request, fetched a subtitle or checked a list). CountToday is what the
/// connection has done since the person's midnight, counting this; Busy is the titles a processor has right now.
/// </summary>
public sealed record ConnectionUsedEvent(
    string Kind,
    string Id,
    string Name,
    int CountToday,
    int Busy,
    string AtUtc);

/// <summary>Payload of <c>AttentionChanged</c>: what moved the bell's count, in a word.</summary>
public sealed record AttentionChangedEvent(string Reason);

/// <summary>Payload of <c>ConnectivityChanged</c>: the internet is down (or back) since a moment, as <c>GET /api/system/connectivity</c> says it.</summary>
public sealed record ConnectivityChangedEvent(bool IsOutage, string? SinceUtc, string CheckedUtc, string Message);

/// <summary>Payload of <c>DownloadHealthChanged</c>: a download-health record changed on this client (empty for every client).</summary>
public sealed record DownloadHealthChangedEvent(string? ClientId);

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
        => PublishWithAttentionAsync(publisher, LiveEventNames.JourneyStageChanged, payload, "journey", cancellationToken);

    public static Task PublishProcessorHandoffChangedAsync(this IRealtimeEventPublisher publisher, ProcessorHandoffChangedEvent payload, CancellationToken cancellationToken)
        => PublishWithAttentionAsync(publisher, LiveEventNames.ProcessorHandoffChanged, payload, "handoff", cancellationToken);

    public static Task PublishRecoveryCaseChangedAsync(this IRealtimeEventPublisher publisher, RecoveryCaseChangedEvent payload, CancellationToken cancellationToken)
        => PublishWithAttentionAsync(publisher, LiveEventNames.RecoveryCaseChanged, payload, "recovery", cancellationToken);

    public static Task PublishJobChangedAsync(this IRealtimeEventPublisher publisher, JobChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.JobChanged, payload, cancellationToken);

    public static Task PublishSearchCycleProgressAsync(this IRealtimeEventPublisher publisher, SearchCycleProgressEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.SearchCycleProgress, payload, cancellationToken);

    public static Task PublishSearchesInFlightChangedAsync(this IRealtimeEventPublisher publisher, SearchesInFlightChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.SearchesInFlightChanged, payload, cancellationToken);

    public static Task PublishIndexerQueryRecordedAsync(this IRealtimeEventPublisher publisher, IndexerQueryRecordedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.IndexerQueryRecorded, payload, cancellationToken);

    public static Task PublishClientStateChangedAsync(this IRealtimeEventPublisher publisher, ClientStateChangedEvent payload, CancellationToken cancellationToken)
        => PublishWithAttentionAsync(publisher, LiveEventNames.ClientStateChanged, payload, "client", cancellationToken);

    public static Task PublishDriveSpaceChangedAsync(this IRealtimeEventPublisher publisher, DriveSpaceChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.DriveSpaceChanged, payload, cancellationToken);

    public static Task PublishReleasesTurnedDownAsync(this IRealtimeEventPublisher publisher, ReleasesTurnedDownEvent payload, CancellationToken cancellationToken)
        => PublishWithAttentionAsync(publisher, LiveEventNames.ReleasesTurnedDown, payload, "noRelease", cancellationToken);

    public static Task PublishConnectionUsedAsync(this IRealtimeEventPublisher publisher, ConnectionUsedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.ConnectionUsed, payload, cancellationToken);

    public static Task PublishDownloadSpeedSampleAsync(this IRealtimeEventPublisher publisher, DownloadSpeedSampleEvent payload, CancellationToken cancellationToken)
        => publisher.PublishLiveEventAsync(LiveEventNames.DownloadSpeedSample, payload, cancellationToken);

    /// <summary>
    /// Sends a Live push and, beside it, the small <c>AttentionChanged</c> on the dashboard subject: the bell
    /// listens to that one, so its count moves with no screen open and without joining Live (which makes the
    /// server read the download clients every second).
    /// </summary>
    private static async Task PublishWithAttentionAsync(IRealtimeEventPublisher publisher, string eventName, object payload, string reason, CancellationToken cancellationToken)
    {
        await publisher.PublishLiveEventAsync(eventName, payload, cancellationToken);
        await publisher.PublishAttentionChangedAsync(reason, cancellationToken);
    }

    /// <summary>Something the bell counts changed; <paramref name="reason"/> says what (recovery, handoff, client, journey, orphans, noRelease, update).</summary>
    public static Task PublishAttentionChangedAsync(this IRealtimeEventPublisher publisher, string reason, CancellationToken cancellationToken)
        => publisher.PublishDashboardEventAsync(LiveEventNames.AttentionChanged, new AttentionChangedEvent(reason), cancellationToken);

    private sealed class AttentionBurst
    {
        public readonly object Lock = new();
        public readonly SortedSet<string> Reasons = new(StringComparer.Ordinal);
        public bool Scheduled;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRealtimeEventPublisher, AttentionBurst> attentionBursts = new();

    /// <summary>How long a burst of changes is gathered into one <c>AttentionChanged</c>.</summary>
    public static readonly TimeSpan AttentionCoalesceWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Asks for one <c>AttentionChanged</c> shortly after a write, so a burst of writes (a search cycle, a bulk dismiss)
    /// sends one push, sent after the last write of the window has been saved. Its reason lists everything that moved,
    /// comma separated. Never waits and never throws.
    /// </summary>
    public static void RequestAttentionChanged(this IRealtimeEventPublisher? publisher, string reason)
    {
        if (publisher is null)
        {
            return;
        }

        var burst = attentionBursts.GetValue(publisher, _ => new AttentionBurst());
        lock (burst.Lock)
        {
            burst.Reasons.Add(reason);
            if (burst.Scheduled)
            {
                return;
            }

            burst.Scheduled = true;
        }

        _ = Task.Run(async () =>
        {
            string joined;
            try
            {
                await Task.Delay(AttentionCoalesceWindow);
            }
            finally
            {
                lock (burst.Lock)
                {
                    joined = string.Join(',', burst.Reasons);
                    burst.Reasons.Clear();
                    burst.Scheduled = false;
                }
            }

            try
            {
                await publisher.PublishAttentionChangedAsync(joined, CancellationToken.None);
            }
            catch
            {
                // The data is already saved; the next push or a reconnect catches the bell up.
            }
        });
    }

    /// <summary>The internet went down or came back.</summary>
    public static Task PublishConnectivityChangedAsync(this IRealtimeEventPublisher publisher, ConnectivityChangedEvent payload, CancellationToken cancellationToken)
        => publisher.PublishDashboardEventAsync(LiveEventNames.ConnectivityChanged, payload, cancellationToken);

    /// <summary>A download-health record was written or cleared.</summary>
    public static Task PublishDownloadHealthChangedAsync(this IRealtimeEventPublisher publisher, string clientId, CancellationToken cancellationToken)
        => publisher.PublishDashboardEventAsync(LiveEventNames.DownloadHealthChanged, new DownloadHealthChangedEvent(clientId), cancellationToken);

    public static async Task PublishConnectionCheckedAsync(this IRealtimeEventPublisher publisher, ConnectionCheckedEvent payload, CancellationToken cancellationToken)
    {
        await publisher.PublishLiveEventAsync(LiveEventNames.ConnectionChecked, payload, cancellationToken);
        await publisher.PublishDashboardEventAsync(LiveEventNames.ConnectionChecked, payload, cancellationToken);
    }
}
