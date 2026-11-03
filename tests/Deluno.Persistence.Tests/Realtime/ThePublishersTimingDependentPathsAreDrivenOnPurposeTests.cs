using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using System.Threading.Channels;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
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

    private sealed class CapturingLogger : ILogger<SignalRRealtimeEventPublisher>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private sealed class Sections(bool empty = false, bool fail = false) : IDashboardSectionSource
    {
        private int _builds;

        public int Builds => Volatile.Read(ref _builds);

        public Task<DashboardSectionBuild> BuildSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _builds);
            if (fail)
            {
                throw new InvalidOperationException("The database is busy.");
            }

            return Task.FromResult(new DashboardSectionBuild(
                empty ? new Dictionary<string, object>() : sections.ToDictionary(name => name, name => (object)new { name }),
                []));
        }
    }

    private sealed class Watchers(int dashboard, int live) : IRealtimeWatchers
    {
        public int Count(string subject) => subject == RealtimeGroups.Dashboard ? dashboard : subject == LiveRealtimeSubject.Name ? live : 0;
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string NoonText = "2026-10-01T12:00:00.0000000+00:00";

    private static bool FlushRunning(SignalRRealtimeEventPublisher publisher)
        => (bool)typeof(SignalRRealtimeEventPublisher).GetField("_flushRunning", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(publisher)!;

    private static JsonElement Body(RealtimeEnvelope envelope) => JsonSerializer.SerializeToElement(envelope.Data);

    /// <summary>Moves the fake clock on in small steps until the condition holds; bounded by the number of tries.</summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5); // reason: lets the publisher's background loop run between fake-clock steps; bounded by the 400 tries
        }

        Assert.True(condition(), $"Gave up waiting for: {what}.");
    }

    private static bool PushedSummary(SignalRRealtimeEventPublisher publisher)
        => publisher.Resume(1, [RealtimeGroups.Dashboard]).Envelopes.Any(e => e.Name == LiveEventNames.DashboardSummaryUpdated);

    private static async Task<RealtimeEnvelope[]> DeliveredAsync(SignalRRealtimeEventPublisher publisher, int expected, params string[] subjects)
    {
        await publisher.StartAsync(CancellationToken.None);
        RealtimeEnvelope[] heard = [];
        await Wait.UntilAsync(() =>
        {
            var result = publisher.Resume(1, subjects);
            heard = result.Status == RealtimeResumeStatus.Replayed ? [.. result.Envelopes] : [];
            return heard.Length >= expected;
        }, $"the publisher to deliver {expected} events");
        return heard;
    }

    private static SignalRRealtimeEventPublisher PublisherOver(
        IHubContext<ActivityHub> hub,
        TimeProvider? clock = null,
        IDashboardSectionSource? sections = null,
        ILogger<SignalRRealtimeEventPublisher>? logger = null,
        int window = SignalRRealtimeEventPublisher.DefaultResumeWindowSize,
        IRealtimeWatchers? watchers = null)
        => new(hub, logger ?? NullLogger<SignalRRealtimeEventPublisher>.Instance, clock ?? TimeProvider.System, window, sections, watchers);

    [Fact]
    public void The_instance_id_is_32_hex_characters_without_dashes()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));

        Assert.Matches("^[0-9a-f]{32}$", publisher.InstanceId);
    }

    [Fact]
    public async Task Every_dispatch_stage_and_every_envelope_carries_the_clock_time_in_round_trip_format()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), new FakeTimeProvider(Noon));
        await publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await publisher.PublishDispatchGrabAttemptAsync("d1", "Release", "c1", "qBittorrent", CancellationToken.None);
        await publisher.PublishDispatchGrabCompletedAsync("d1", "Release", "c1", true, null, CancellationToken.None);
        await publisher.PublishDispatchDetectedAsync("d1", "Release", "abc", 10, CancellationToken.None);
        await publisher.PublishDispatchImportStartedAsync("d1", "Release", "movies", CancellationToken.None);
        await publisher.PublishDispatchImportCompletedAsync("d1", "Release", true, "/films/Release.mkv", null, CancellationToken.None);

        var heard = await DeliveredAsync(publisher, 5, "queue");

        Assert.All(heard, item =>
        {
            Assert.Equal(NoonText, item.At);
            Assert.Equal(NoonText, Body(item).GetProperty("timestamp").GetString());
        });
    }

    [Fact]
    public async Task A_title_change_names_its_library_only_when_it_has_exactly_one()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));
        await publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await publisher.PublishTitleChangedAsync("Movie", "m2", ["l1"], CancellationToken.None);
        await publisher.PublishTitleChangedAsync("Movie", "m3", ["l1", "l2"], CancellationToken.None);

        var heard = await DeliveredAsync(publisher, 2, RealtimeGroups.Dashboard);

        var single = heard.Single(e => Body(e).GetProperty("id").GetString() == "m2");
        var several = heard.Single(e => Body(e).GetProperty("id").GetString() == "m3");
        Assert.Equal("l1", Body(single).GetProperty("libraryId").GetString());
        Assert.Equal(JsonValueKind.Null, Body(several).GetProperty("libraryId").ValueKind);
    }

    [Fact]
    public async Task A_series_change_goes_to_its_own_title_subject_and_an_import_with_no_title_id_goes_only_to_the_queue()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));
        await publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await publisher.PublishEntityChangedAsync("Series", "s1", CancellationToken.None);
        await publisher.PublishImportStateChangedAsync("job-1", "finished", "movie", "  ", "Film", null, "2026-10-01T12:00:00Z", CancellationToken.None);

        await DeliveredAsync(publisher, 2, "queue", "title:s1");

        Assert.Contains(publisher.Resume(1, ["title:s1"]).Envelopes, e => e.Name == "SeriesChanged");
        Assert.Empty(publisher.Resume(1, ["title:  "]).Envelopes);
        Assert.Single(publisher.Resume(1, ["queue"]).Envelopes, e => e.Name == "ImportStateChanged");
    }

    [Fact]
    public async Task A_delivery_that_fails_is_logged_with_the_event_name_and_the_error()
    {
        var logger = new CapturingLogger();
        using var publisher = PublisherOver(new Hub((_, _) => throw new InvalidOperationException("The connection dropped.")), logger: logger);
        await publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);

        await publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => logger.Entries.Count > 0, "the failed delivery to be logged");

        var entry = logger.Entries.Single();
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Realtime event QueueItemRemoved could not be delivered.", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Error);
    }

    [Fact]
    public async Task A_listener_that_throws_is_logged_and_does_not_stop_the_publish()
    {
        var logger = new CapturingLogger();
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), logger: logger);
        publisher.EventPublished += _ => throw new InvalidOperationException("The listener broke.");

        await publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);

        Assert.Equal(1, publisher.CurrentSequence);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("A listener of realtime event QueueItemRemoved failed.", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Error);
    }

    [Fact]
    public async Task A_dropped_event_is_logged_and_counted_with_the_event_name()
    {
        var logger = new CapturingLogger();
        var tags = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "deluno.realtime.events.dropped")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, measurementTags, _) =>
        {
            foreach (var tag in measurementTags)
            {
                if (tag.Key == "event.name")
                {
                    tags.Enqueue(tag.Value as string);
                }
            }
        });
        listener.Start();
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), logger: logger);

        for (var i = 0; i < 1001; i++)
        {
            await publisher.PublishQueueItemRemovedAsync($"q{i}", CancellationToken.None);
        }

        Assert.Contains("QueueItemRemoved", tags);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("A realtime event was dropped because the outbound queue is saturated (latest QueueItemRemoved).", entry.Message);
    }

    [Fact]
    public async Task A_change_with_no_section_source_or_no_matching_section_starts_no_flush()
    {
        using var withoutSource = PublisherOver(new Hub((_, _) => Task.CompletedTask), new FakeTimeProvider(Noon));
        await withoutSource.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);
        Assert.False(FlushRunning(withoutSource));

        using var unmapped = PublisherOver(new Hub((_, _) => Task.CompletedTask), new FakeTimeProvider(Noon), new Sections());
        await unmapped.PublishDashboardEventAsync("SomethingNobodyReads", new { }, CancellationToken.None);
        Assert.False(FlushRunning(unmapped));

        using var mapped = PublisherOver(new Hub((_, _) => Task.CompletedTask), new FakeTimeProvider(Noon), new Sections());
        await mapped.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);
        Assert.True(FlushRunning(mapped));
    }

    [Fact]
    public async Task A_page_watching_only_the_live_subject_still_gets_the_summary_and_it_carries_the_clock_time()
    {
        var clock = new FakeTimeProvider(Noon);
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), clock, new Sections(), watchers: new Watchers(dashboard: 0, live: 1));
        await publisher.StartAsync(CancellationToken.None);
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);

        await AdvanceUntilAsync(clock, () => PushedSummary(publisher), "the summary to be pushed to a live-only page");

        var pushed = publisher.Resume(1, [RealtimeGroups.Dashboard]).Envelopes.First(e => e.Name == LiveEventNames.DashboardSummaryUpdated);
        var asOf = Body(pushed).GetProperty("asOfUtc").GetString();
        Assert.True(DateTimeOffset.TryParseExact(asOf, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), $"asOfUtc was {asOf}");
    }

    [Fact]
    public async Task A_page_watching_nothing_gets_no_summary_built()
    {
        var clock = new FakeTimeProvider(Noon);
        var sections = new Sections();
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), clock, sections, watchers: new Watchers(dashboard: 0, live: 0));
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);

        await AdvanceUntilAsync(clock, () => !FlushRunning(publisher), "the flush to find nobody watching and end");

        Assert.Equal(0, sections.Builds);
    }

    [Fact]
    public async Task A_build_with_no_sections_pushes_nothing()
    {
        var clock = new FakeTimeProvider(Noon);
        var sections = new Sections(empty: true);
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), clock, sections);
        await publisher.StartAsync(CancellationToken.None);
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);

        await AdvanceUntilAsync(clock, () => sections.Builds >= 1 && !FlushRunning(publisher), "the empty build to finish and the flush to end");

        Assert.False(PushedSummary(publisher));
    }

    [Fact]
    public async Task A_section_source_that_fails_is_logged()
    {
        var clock = new FakeTimeProvider(Noon);
        var logger = new CapturingLogger();
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), clock, new Sections(fail: true), logger);
        await publisher.PublishHealthChangedAsync("indexer", "ok", "Up", CancellationToken.None);

        await AdvanceUntilAsync(clock, () => logger.Entries.Count > 0, "the failed build to be logged");

        var entry = logger.Entries.First();
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Dashboard sections could not be pushed.", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Error);
    }

    [Fact]
    public async Task The_resume_window_keeps_exactly_the_configured_number_of_events()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask), window: 3);
        for (var i = 1; i <= 4; i++)
        {
            await publisher.PublishQueueItemRemovedAsync($"q{i}", CancellationToken.None);
        }

        await publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => publisher.Resume(3, ["queue"]).Envelopes.Count == 1, "all four events to be delivered");

        var resumed = publisher.Resume(1, ["queue"]);
        Assert.Equal(RealtimeResumeStatus.Replayed, resumed.Status);
        Assert.Equal([2L, 3L, 4L], resumed.Envelopes.Select(e => e.Seq));
    }

    [Fact]
    public void A_client_with_no_position_is_always_told_to_resync()
    {
        using var publisher = PublisherOver(new Hub((_, _) => Task.CompletedTask));

        Assert.Equal(RealtimeResumeStatus.ResyncRequired, publisher.Resume(0, ["queue"]).Status);
    }

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
