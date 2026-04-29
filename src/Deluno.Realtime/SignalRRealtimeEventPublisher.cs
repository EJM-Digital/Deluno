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
