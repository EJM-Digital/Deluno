using System.Reflection;
using Deluno.Contracts;
using System.Threading.Channels;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The paths of the push publisher that the other tests reach only when a race happens to fall one way (a stop arriving
/// while a send is in flight, a timer that is cancelled, a queue that overflows), each driven on purpose through the hub,
/// the clock and the queue so the coverage of that file is the same on every run (CI run 41682185185).
/// </summary>
public sealed class ThePublishersTimingDependentPathsAreDrivenOnPurposeTests
{
    private sealed class Hub(Func<string, CancellationToken, Task> send) : IHubContext<ActivityHub>
    {
        public IHubClients Clients => new HubClients(send);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class HubClients(Func<string, CancellationToken, Task> send) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => new Proxy(groupName, send);

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class Proxy(string group, Func<string, CancellationToken, Task> send) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken) => send(group, cancellationToken);
    }

    /// <summary>A clock whose timers can be made to fail as a cancelled one does.</summary>
    private sealed class TimerClock(FakeTimeProvider inner) : TimeProvider
    {
        private int _created;

        public bool CancelTimers { get; set; }

        public int TimersCreated => Volatile.Read(ref _created);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (CancelTimers)
            {
                throw new OperationCanceledException("The timer was cancelled.");
            }

            Interlocked.Increment(ref _created);
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class EmptySections : IDashboardSectionSource
    {
        public Task<DashboardSectionBuild> BuildSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken)
            => Task.FromResult(new DashboardSectionBuild(new Dictionary<string, object>(), []));
    }

    private static SignalRRealtimeEventPublisher PublisherOver(IHubContext<ActivityHub> hub, TimeProvider? clock = null, IDashboardSectionSource? sections = null)
        => new(hub, NullLogger<SignalRRealtimeEventPublisher>.Instance, clock ?? TimeProvider.System, SignalRRealtimeEventPublisher.DefaultResumeWindowSize, sections);

    [Fact]
    public async Task A_stop_that_arrives_while_a_send_is_in_flight_ends_the_loop_as_cancelled_not_as_a_failed_delivery()
    {
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new Hub((_, token) =>
        {
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => inFlight.TrySetCanceled(token));
            sending.TrySetResult();
            return inFlight.Task;
        });
        using var publisher = PublisherOver(hub);
        await publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);
        await publisher.StartAsync(CancellationToken.None);
        await sending.Task.WaitAsync(Wait.DefaultLimit);

        await publisher.StopAsync(CancellationToken.None);

        Assert.True(publisher.ExecuteTask!.IsCanceled);
    }

    [Fact]
    public async Task A_queue_that_has_been_closed_ends_the_loop_normally()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));
        var events = (Channel<RealtimeEnvelope>)typeof(SignalRRealtimeEventPublisher)
            .GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(publisher)!;
        events.Writer.Complete();

        await publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => publisher.ExecuteTask is { IsCompletedSuccessfully: true }, "the loop to end once its queue is closed");
    }

    [Fact]
    public async Task A_cancelled_coalescing_timer_clears_the_flush_flag_so_the_next_change_starts_a_new_flush()
    {
        var clock = new TimerClock(new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))) { CancelTimers = true };
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), clock, new EmptySections());
        var running = typeof(SignalRRealtimeEventPublisher).GetField("_flushRunning", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await publisher.PublishHealthChangedAsync("indexer", "problem", "Down", CancellationToken.None);
        await Wait.UntilAsync(() => !(bool)running.GetValue(publisher)!, "the cancelled flush to clear its running flag");

        clock.CancelTimers = false;
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);
        await Wait.UntilAsync(() => clock.TimersCreated >= 1, "a new flush to start its coalescing timer");
    }

    [Fact]
    public async Task Library_movie_and_series_changes_and_title_changes_reach_the_subjects_that_listen_for_them()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));
        await publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await publisher.PublishEntityChangedAsync("Library", "l1", CancellationToken.None);
        await publisher.PublishEntityChangedAsync("Movie", "m1", CancellationToken.None);
        await publisher.PublishEntityChangedAsync("Series", "*", CancellationToken.None);
        await publisher.PublishTitleChangedAsync("Movie", "m2", ["l1"], CancellationToken.None);
        await publisher.PublishTitleChangedAsync("Movie", "m3", ["l1", "l2"], CancellationToken.None);

        await publisher.StartAsync(CancellationToken.None);
        string[] subjects = [RealtimeGroups.Dashboard, RealtimeGroups.Library("l1"), RealtimeGroups.Library("l2"), RealtimeGroups.Title("m1"), RealtimeGroups.Title("m2"), RealtimeGroups.Title("m3")];
        await Wait.UntilAsync(() => publisher.Resume(1, subjects).Envelopes.Count >= 12, "all the changes to be delivered");

        var heard = publisher.Resume(1, subjects).Envelopes;
        Assert.Contains(heard, e => e.Name == "LibraryChanged" && e.Subject == RealtimeGroups.Library("l1"));
        Assert.Contains(heard, e => e.Name == "LibraryChanged" && e.Subject == RealtimeGroups.Dashboard);
        Assert.Contains(heard, e => e.Name == "MovieChanged" && e.Subject == RealtimeGroups.Title("m1"));
        Assert.DoesNotContain(heard, e => e.Subject == RealtimeGroups.Title("*"));
        Assert.Contains(heard, e => e.Name == "MovieChanged" && e.Subject == RealtimeGroups.Library("l2"));
    }

    [Fact]
    public async Task A_queue_that_overflows_before_the_loop_starts_keeps_the_newest_events_and_every_sequence_number()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));
        for (var i = 1; i <= 1005; i++)
        {
            await publisher.PublishQueueItemRemovedAsync($"q{i}", CancellationToken.None);
        }

        Assert.Equal(1005, publisher.CurrentSequence);

        await publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => publisher.Resume(1004, ["queue"]).Envelopes.Count == 1, "the newest event to be delivered");

        Assert.Equal(RealtimeResumeStatus.ResyncRequired, publisher.Resume(1, ["queue"]).Status);
    }
}
