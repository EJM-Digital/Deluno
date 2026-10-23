using System.Collections.Concurrent;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// A push that cannot be delivered to its group is logged and skipped, the next event is delivered as usual, the event is still kept for
/// a reconnecting page to replay, and the publisher stops cleanly when the host does (coverage register, engines). Refs #1296.
/// </summary>
public sealed class AFailedDeliveryDoesNotStopTheRealtimePublisherTests
{
    private sealed class Hub : IHubContext<ActivityHub>
    {
        public ConcurrentQueue<(string Group, object? Envelope)> Delivered { get; } = new();

        public string FailingGroup { get; set; } = string.Empty;

        public IHubClients Clients => new HubClients(this);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class HubClients(Hub hub) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => new Proxy(hub, groupName);

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class Proxy(Hub hub, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken)
        {
            if (group == hub.FailingGroup)
            {
                throw new InvalidOperationException("The connection dropped while sending.");
            }

            hub.Delivered.Enqueue((group, args.SingleOrDefault()));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_group_that_cannot_be_reached_is_skipped_the_next_event_is_delivered_and_the_publisher_stops_cleanly()
    {
        var hub = new Hub { FailingGroup = "queue" };
        using var publisher = new SignalRRealtimeEventPublisher(hub, NullLogger<SignalRRealtimeEventPublisher>.Instance, TimeProvider.System);
        await publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);

        await publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => publisher.Resume(1, ["queue", "dashboard"]).Envelopes.Count >= 2, "the publisher to work through all three events");

        Assert.Contains(hub.Delivered, item => item.Group == "dashboard" && item.Envelope is RealtimeEnvelope { Name: "HealthChanged" });
        Assert.DoesNotContain(hub.Delivered, item => item.Group == "queue");
        Assert.Contains(publisher.Resume(1, ["queue"]).Envelopes, envelope => envelope.Name == "QueueItemRemoved");
        Assert.False(publisher.ExecuteTask is { IsCompleted: true }, "a failed delivery must not end the loop");

        await publisher.StopAsync(CancellationToken.None);

        Assert.True(publisher.ExecuteTask is { IsCompleted: true });
        Assert.False(publisher.ExecuteTask!.IsFaulted);
    }
}
