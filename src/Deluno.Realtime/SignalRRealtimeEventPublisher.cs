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
    int resumeWindowSize = SignalRRealtimeEventPublisher.DefaultResumeWindowSize)
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
    {
        Enqueue(
            "ActivityEventAdded",
            RealtimeGroups.Activity,
            new
            {
                id,
                message,
                category,
                severity,
                createdUtc
            });
        return Task.CompletedTask;
    }

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
        Enqueue(
            "SearchRunCompleted",
            RealtimeGroups.Library(libraryId),
            new
            {
                libraryId,
                libraryName,
                mediaType,
                plannedCount,
                queuedCount,
                skippedCount,
                completedUtc
            });
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
        var subject = string.Equals(entityType, "Library", StringComparison.OrdinalIgnoreCase)
            ? RealtimeGroups.Library(entityId)
            : RealtimeGroups.Dashboard;

        Enqueue(
            $"{entityType}Changed",
            subject,
            new
            {
                id = entityId
            });
        return Task.CompletedTask;
    }

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
