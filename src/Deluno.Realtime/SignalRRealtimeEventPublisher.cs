using Deluno.Infrastructure.Observability;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Deluno.Realtime;

public sealed class SignalRRealtimeEventPublisher(
    IHubContext<ActivityHub> hubContext,
    ILogger<SignalRRealtimeEventPublisher> logger)
    : BackgroundService, IRealtimeEventPublisher
{
    private readonly Channel<RealtimeEnvelope> _events = Channel.CreateBounded<RealtimeEnvelope>(
        new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public Task PublishHealthChangedAsync(
        string source,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        Enqueue(
            "HealthChanged",
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
            new
            {
                dispatchId,
                releaseName,
                clientId,
                clientName,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
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
            new
            {
                dispatchId,
                releaseName,
                clientId,
                succeeded,
                message,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
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
            new
            {
                dispatchId,
                releaseName,
                torrentHash,
                downloadedBytes,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
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
            new
            {
                dispatchId,
                releaseName,
                mediaType,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
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
            new
            {
                dispatchId,
                releaseName,
                succeeded,
                importedPath,
                failureReason,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            });
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _events.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await hubContext.Clients.All.SendAsync(envelope.EventName, envelope.Payload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Realtime event {EventName} could not be delivered.", envelope.EventName);
            }
        }
    }

    private void Enqueue(string eventName, object payload)
    {
        if (!_events.Writer.TryWrite(new RealtimeEnvelope(eventName, payload)))
        {
            DelunoObservability.RealtimeEventsDropped.Add(
                1,
                [new KeyValuePair<string, object?>("event.name", eventName)]);
            logger.LogDebug("Realtime event {EventName} was dropped because the outbound queue is saturated.", eventName);
        }
    }

    private sealed record RealtimeEnvelope(string EventName, object Payload);
}
