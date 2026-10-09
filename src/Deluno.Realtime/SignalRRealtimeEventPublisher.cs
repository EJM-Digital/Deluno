using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Deluno.Realtime;

public sealed class SignalRRealtimeEventPublisher(
    IHubContext<ActivityHub> hubContext,
    ILogger<SignalRRealtimeEventPublisher> logger,
    TimeProvider timeProvider,
    int resumeWindowSize = SignalRRealtimeEventPublisher.DefaultResumeWindowSize,
    IDashboardSectionSource? sectionSource = null,
    IRealtimeWatchers? watchers = null)
    : BackgroundService, IRealtimeEventPublisher, IRealtimeResumeSource
{
    /// <summary>How many envelopes a reconnecting client can resume across.</summary>
    public const int DefaultResumeWindowSize = 5000;

    private readonly Channel<RealtimeEnvelope> _events = Channel.CreateBounded<RealtimeEnvelope>(
        new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    /// <summary>
    /// Raised with the event name each time an event is published, on the thread that published it. For in-process
    /// listeners that follow server pushes without a socket, such as the Windows tray icon's needs-you mark.
    /// A listener must return quickly and must not throw.
    /// </summary>
    public event Action<string>? EventPublished;

    private readonly object _ringLock = new();
    private readonly Queue<RealtimeEnvelope> _ring = new();
    private long _seq;

    public Task PublishHealthChangedAsync(
        string source,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "HealthChanged",
            RealtimeGroups.Dashboard,
            new
            {
                source,
                status,
                message
            });
        return Task.CompletedTask;
    }

    public Task PublishDownloadProgressAsync(
        string id,
        string title,
        double progress,
        double speedMbps,
        string? eta,
        string status,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DownloadProgress",
            RealtimeGroups.Queue,
            new
            {
                id,
                title,
                progress,
                speedMbps,
                eta,
                status
            });
        return Task.CompletedTask;
    }

    public Task PublishActivityEventAddedAsync(
        string id,
        string message,
        string category,
        string severity,
        string createdUtc,
        CancellationToken cancellationToken)
        => PublishActivityEventAddedAsync(new ActivityEventPush(id, message, category, severity, createdUtc), cancellationToken);

    public Task PublishActivityEventAddedAsync(ActivityEventPush push, CancellationToken cancellationToken)
    {
        Enqueue(
            "ActivityEventAdded",
            RealtimeGroups.Activity,
            new
            {
                id = push.Id,
                message = push.Message,
                category = push.Category,
                label = Deluno.Contracts.ActivityEventLabels.For(push.Category),
                severity = push.Severity,
                createdUtc = push.CreatedUtc,
                relatedEntityType = push.RelatedEntityType,
                relatedEntityId = push.RelatedEntityId,
                relatedJobId = push.RelatedJobId,
                title = push.Title,
                filePath = push.FilePath,
                sourceName = push.SourceName,
                reasonGroup = push.ReasonGroup,
                type = ActivityHistory.TypeOf(push.Category),
                source = ActivityHistory.SourceOf(push.Category),
                outcome = ActivityHistory.OutcomeOf(push.Category),
                detailsJson = ShortDetails(push.DetailsJson)
            });
        return Task.CompletedTask;
    }

    /// <summary>The details a row opens with, only when small enough to ride along on every push.</summary>
    private static string? ShortDetails(string? detailsJson)
        => string.IsNullOrWhiteSpace(detailsJson) || detailsJson.Length > 4096 ? null : detailsJson;

    public Task PublishQueueItemAddedAsync(
        string id,
        string title,
        string type,
        string status,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "QueueItemAdded",
            RealtimeGroups.Queue,
            new
            {
                id,
                title,
                type,
                status
            });
        return Task.CompletedTask;
    }

    public Task PublishQueueItemRemovedAsync(string id, CancellationToken cancellationToken)
    {
        Enqueue(
            "QueueItemRemoved",
            RealtimeGroups.Queue,
            new
            {
                id
            });
        return Task.CompletedTask;
    }

    public Task PublishQueueItemStatusChangedAsync(
        string id,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "QueueItemStatusChanged",
            RealtimeGroups.Queue,
            new
            {
                id,
                status,
                errorMessage
            });
        return Task.CompletedTask;
    }

    public Task PublishSearchRunCompletedAsync(
        string libraryId,
        string libraryName,
        string mediaType,
        int plannedCount,
        int queuedCount,
        int skippedCount,
        string completedUtc,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            libraryId,
            libraryName,
            mediaType,
            plannedCount,
            queuedCount,
            skippedCount,
            completedUtc
        };
        Enqueue("SearchRunCompleted", RealtimeGroups.Library(libraryId), payload);
        // The search schedule lists every library, so it listens on "dashboard" instead of joining each one (#1179).
        Enqueue("SearchRunCompleted", RealtimeGroups.Dashboard, payload);
        return Task.CompletedTask;
    }

    public Task PublishImportStateChangedAsync(
        string jobId,
        string state,
        string? entityType,
        string? entityId,
        string? title,
        string? errorMessage,
        string changedUtc,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "ImportStateChanged",
            RealtimeGroups.Queue,
            new
            {
                jobId,
                state,
                entityType,
                entityId,
                title,
                errorMessage,
                changedUtc
            });

        if (TitleIdOf(entityType, entityId) is { } titleId)
        {
            Enqueue(
                "ImportStateChanged",
                RealtimeGroups.Title(titleId),
                new { jobId, state, entityType, entityId, title, errorMessage, changedUtc });
        }

        return Task.CompletedTask;
    }

    public Task PublishDispatchGrabAttemptAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        string clientName,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DispatchGrabAttempt",
            RealtimeGroups.Queue,
            new
            {
                dispatchId,
                releaseName,
                clientId,
                clientName,
                timestamp = timeProvider.GetUtcNow().ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task PublishDispatchGrabCompletedAsync(
        string dispatchId,
        string releaseName,
        string clientId,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DispatchGrabCompleted",
            RealtimeGroups.Queue,
            new
            {
                dispatchId,
                releaseName,
                clientId,
                succeeded,
                message,
                timestamp = timeProvider.GetUtcNow().ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task PublishDispatchDetectedAsync(
        string dispatchId,
        string releaseName,
        string? torrentHash,
        long? downloadedBytes,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DispatchDetected",
            RealtimeGroups.Queue,
            new
            {
                dispatchId,
                releaseName,
                torrentHash,
                downloadedBytes,
                timestamp = timeProvider.GetUtcNow().ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task PublishDispatchImportStartedAsync(
        string dispatchId,
        string releaseName,
        string mediaType,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DispatchImportStarted",
            RealtimeGroups.Queue,
            new
            {
                dispatchId,
                releaseName,
                mediaType,
                timestamp = timeProvider.GetUtcNow().ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task PublishDispatchImportCompletedAsync(
        string dispatchId,
        string releaseName,
        bool succeeded,
        string? importedPath,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "DispatchImportCompleted",
            RealtimeGroups.Queue,
            new
            {
                dispatchId,
                releaseName,
                succeeded,
                importedPath,
                failureReason,
                timestamp = timeProvider.GetUtcNow().ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task PublishEntityChangedAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var isLibrary = string.Equals(entityType, "Library", StringComparison.OrdinalIgnoreCase);
        var subject = isLibrary
            ? RealtimeGroups.Library(entityId)
            : RealtimeGroups.Dashboard;

        Enqueue(
            $"{entityType}Changed",
            subject,
            new
            {
                id = entityId
            });

        if (IsTitleType(entityType) && entityId != "*")
        {
            Enqueue($"{entityType}Changed", RealtimeGroups.Title(entityId), new { id = entityId });
        }

        if (isLibrary)
        {
            // Settings > Libraries and the Dashboard join "dashboard", not a library:{id} subject per library (#1179).
            Enqueue($"{entityType}Changed", RealtimeGroups.Dashboard, new { id = entityId });
        }

        return Task.CompletedTask;
    }

    public Task PublishTitleChangedAsync(
        string entityType,
        string entityId,
        IReadOnlyCollection<string> libraryIds,
        CancellationToken cancellationToken)
    {
        var libraryId = libraryIds.Count == 1 ? libraryIds.First() : null;
        var name = $"{entityType}Changed";
        Enqueue(name, RealtimeGroups.Dashboard, new { id = entityId, libraryId });
        Enqueue(name, RealtimeGroups.Title(entityId), new { id = entityId, libraryId });
        foreach (var id in libraryIds)
        {
            Enqueue(name, RealtimeGroups.Library(id), new { id = entityId, libraryId = id });
        }

        return Task.CompletedTask;
    }

    private static bool IsTitleType(string entityType)
        => string.Equals(entityType, "Movie", StringComparison.OrdinalIgnoreCase)
           || string.Equals(entityType, "Series", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The film or show a pushed event is about, so a copy can go to that title's own subject (#1179).
    /// Episodes are not a title page; their show hears about them from the write itself.
    /// </summary>
    private static string? TitleIdOf(string? entityType, string? entityId)
        => !string.IsNullOrWhiteSpace(entityId) && entityType is not null
           && (string.Equals(entityType, "movie", StringComparison.OrdinalIgnoreCase)
               || string.Equals(entityType, "series", StringComparison.OrdinalIgnoreCase)
               || string.Equals(entityType, "show", StringComparison.OrdinalIgnoreCase))
            ? entityId
            : null;

    private static string? TitleIdOf(object payload) => payload switch
    {
        JourneyStageChangedEvent journey => TitleIdOf(journey.EntityType, journey.EntityId),
        RecoveryCaseChangedEvent recovery => TitleIdOf(recovery.EntityType, recovery.EntityId),
        ReleasesTurnedDownEvent turnedDown => TitleIdOf(turnedDown.EntityType, turnedDown.EntityId),
        _ => null
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _events.Reader.ReadAllAsync(stoppingToken))
        {
            AddToResumeWindow(envelope);
            try
            {
                await hubContext.Clients.Group(envelope.Subject).SendAsync("RealtimeEvent", envelope, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Realtime event {EventName} could not be delivered.", envelope.Name);
            }
        }
    }

    /// <summary>
    /// Stamps the envelope with the next sequence number before it is ever
    /// queued, so a sequence is reserved for every event this publisher is
    /// asked to send — even one later evicted from the bounded channel by
    /// <see cref="BoundedChannelFullMode.DropOldest"/> before a reader sees
    /// it. That eviction then shows up as a genuine, detectable gap in the
    /// resume window instead of silently renumbering later events.
    /// </summary>
    private void Enqueue(string eventName, string subject, object payload)
    {
        var envelope = new RealtimeEnvelope(
            Interlocked.Increment(ref _seq),
            eventName,
            subject,
            timeProvider.GetUtcNow().ToString("O"),
            payload);

        if (!_events.Writer.TryWrite(envelope))
        {
            DelunoObservability.RealtimeEventsDropped.Add(
                1,
                [new KeyValuePair<string, object?>("event.name", eventName)]);
            logger.LogDebug("Realtime event {EventName} was dropped because the outbound queue is saturated.", eventName);
        }

        if (!string.Equals(eventName, LiveEventNames.DashboardSummaryUpdated, StringComparison.Ordinal))
        {
            NoteDashboardChange(eventName);
        }

        try
        {
            EventPublished?.Invoke(eventName);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "A listener of realtime event {EventName} failed.", eventName);
        }
    }

    /// <summary>
    /// Which Dashboard summary sections a change can move (#1097). An event that
    /// maps to nothing never asks the Dashboard for anything.
    /// </summary>
    internal static IReadOnlyCollection<string> SectionsFor(string eventName) => eventName switch
    {
        "HealthChanged" => [DashboardSectionNames.Health, DashboardSectionNames.System, DashboardSectionNames.Indexers],
        "DownloadProgress" or "QueueItemAdded" or "QueueItemRemoved" or "QueueItemStatusChanged"
            => [DashboardSectionNames.Downloads],
        "ActivityEventAdded" => [DashboardSectionNames.Activity],
        "SearchRunCompleted" => [DashboardSectionNames.Automation, DashboardSectionNames.Indexers],
        "ImportStateChanged" => [DashboardSectionNames.Imports, DashboardSectionNames.Downloads],
        "DispatchGrabAttempt" or "DispatchGrabCompleted" or "DispatchDetected"
            or "DispatchImportStarted" or "DispatchImportCompleted"
            => [DashboardSectionNames.Pipeline, DashboardSectionNames.Downloads, DashboardSectionNames.Imports],
        LiveEventNames.JourneyStageChanged
            => [DashboardSectionNames.Pipeline, DashboardSectionNames.Downloads, DashboardSectionNames.Imports],
        LiveEventNames.ProcessorHandoffChanged => [DashboardSectionNames.Processors, DashboardSectionNames.Imports],
        LiveEventNames.RecoveryCaseChanged => [DashboardSectionNames.Imports],
        LiveEventNames.JobChanged or LiveEventNames.SearchCycleProgress => [DashboardSectionNames.Automation],
        LiveEventNames.IndexerQueryRecorded => [DashboardSectionNames.Indexers],
        LiveEventNames.ClientStateChanged => [DashboardSectionNames.Downloads, DashboardSectionNames.Health],
        LiveEventNames.DriveSpaceChanged => [DashboardSectionNames.Storage],
        "MovieChanged" or "SeriesChanged" or "EpisodeChanged" or "LibraryChanged"
            => [DashboardSectionNames.Library],
        "IndexerChanged" or "IndexerProxyChanged" => [DashboardSectionNames.Indexers],
        "DownloadClientChanged" => [DashboardSectionNames.Downloads, DashboardSectionNames.Health],
        "AutomationStateChanged" or "IntakeSourceChanged" => [DashboardSectionNames.Automation],
        "SettingsChanged" => DashboardSectionNames.All,
        _ => []
    };

    /// <summary>
    /// The most one burst of changes is held back before the changed sections
    /// are pushed, so a flurry becomes one push per section rather than one per
    /// change. This is a coalescing window, not a rate limit: a change after a
    /// quiet spell is pushed within this window, never after a wait.
    /// </summary>
    public static readonly TimeSpan DashboardCoalesceWindow = TimeSpan.FromMilliseconds(100);

    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pendingSections = new(StringComparer.Ordinal);
    private bool _flushRunning;

    /// <summary>
    /// Pushes the changed summary sections themselves (#1097): the sections a
    /// change can move are marked, and after <see cref="DashboardCoalesceWindow"/>
    /// they are built once and sent as one <c>DashboardSummaryUpdated</c> event
    /// carrying the data. The web writes them straight into its cache, with no
    /// fetch afterwards. Nothing is built while no screen watches.
    /// </summary>
    private void NoteDashboardChange(string eventName)
    {
        if (sectionSource is null)
        {
            return;
        }

        var sections = SectionsFor(eventName);
        if (sections.Count == 0)
        {
            return;
        }

        lock (_pendingLock)
        {
            foreach (var section in sections)
            {
                _pendingSections.Add(section);
            }

            if (_flushRunning)
            {
                return;
            }

            _flushRunning = true;
        }

        _ = Task.Run(FlushSectionsAsync);
    }

    private bool IsSomeoneWatching()
        => watchers is null
           || watchers.Count(RealtimeGroups.Dashboard) > 0
           || watchers.Count(LiveRealtimeSubject.Name) > 0;

    private async Task FlushSectionsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(DashboardCoalesceWindow, timeProvider);

                string[] batch;
                lock (_pendingLock)
                {
                    if (_pendingSections.Count == 0)
                    {
                        _flushRunning = false;
                        return;
                    }

                    batch = [.. _pendingSections];
                    _pendingSections.Clear();
                }

                if (!IsSomeoneWatching())
                {
                    continue;
                }

                try
                {
                    var built = await sectionSource!.BuildSectionsAsync(batch, CancellationToken.None);
                    if (built.Sections.Count > 0)
                    {
                        Enqueue(
                            LiveEventNames.DashboardSummaryUpdated,
                            RealtimeGroups.Dashboard,
                            new
                            {
                                asOfUtc = timeProvider.GetUtcNow().ToString("O"),
                                sections = built.Sections,
                                unavailable = built.Unavailable
                            });
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Dashboard sections could not be pushed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_pendingLock)
            {
                _flushRunning = false;
            }
        }
    }

    public Task PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        Enqueue(eventName, RealtimeGroups.Dashboard, payload);
        return Task.CompletedTask;
    }

    public Task PublishLiveEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        Enqueue(eventName, LiveRealtimeSubject.Name, payload);
        if (TitleIdOf(payload) is { } titleId)
        {
            Enqueue(eventName, RealtimeGroups.Title(titleId), payload);
        }

        return Task.CompletedTask;
    }

    private void AddToResumeWindow(RealtimeEnvelope envelope)
    {
        lock (_ringLock)
        {
            _ring.Enqueue(envelope);
            while (_ring.Count > resumeWindowSize)
            {
                _ring.Dequeue();
            }
        }
    }

    public long CurrentSequence => Interlocked.Read(ref _seq);

    public RealtimeResumeResult Resume(long lastSeq, IReadOnlyCollection<string> subjects)
    {
        lock (_ringLock)
        {
            if (lastSeq <= 0)
            {
                return new RealtimeResumeResult(RealtimeResumeStatus.ResyncRequired, []);
            }

            if (lastSeq >= Interlocked.Read(ref _seq))
            {
                return new RealtimeResumeResult(RealtimeResumeStatus.CaughtUp, []);
            }

            var oldestBuffered = _ring.Count > 0 ? _ring.Peek().Seq : long.MaxValue;
            if (lastSeq < oldestBuffered - 1)
            {
                return new RealtimeResumeResult(RealtimeResumeStatus.ResyncRequired, []);
            }

            var missed = _ring
                .Where(envelope => envelope.Seq > lastSeq && subjects.Contains(envelope.Subject, StringComparer.Ordinal))
                .ToArray();
            return new RealtimeResumeResult(RealtimeResumeStatus.Replayed, missed);
        }
    }
}
