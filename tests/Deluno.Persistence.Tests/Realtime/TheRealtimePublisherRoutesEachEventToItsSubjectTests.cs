using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The server's push publisher (coverage register, engines): each event goes to the subject its screen listens on (queue events and the
/// five dispatch stages to the queue, a finished search to its library and to the dashboard), the dashboard sections an event can move
/// are the ones the map names, a listener in the process hears every event and cannot break publishing by throwing, and when the
/// outbound queue overflows the oldest events are dropped, counted, and leave a gap a reconnecting page resyncs from. Refs #1296.
/// </summary>
public sealed class TheRealtimePublisherRoutesEachEventToItsSubjectTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher _publisher;

    public TheRealtimePublisherRoutesEachEventToItsSubjectTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        _services = services.BuildServiceProvider();
        _publisher = new SignalRRealtimeEventPublisher(
            _services.GetRequiredService<IHubContext<ActivityHub>>(),
            NullLogger<SignalRRealtimeEventPublisher>.Instance,
            TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync(CancellationToken.None);
        _publisher.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>Starts the publisher and waits until it has processed everything published so far, then reads the whole stream for these subjects.</summary>
    private async Task<RealtimeEnvelope[]> HeardAsync(int expected, params string[] subjects)
    {
        await _publisher.StartAsync(CancellationToken.None);
        RealtimeEnvelope[] heard = [];
        await Wait.UntilAsync(() =>
        {
            var result = _publisher.Resume(1, subjects);
            heard = result.Status == RealtimeResumeStatus.Replayed ? [.. result.Envelopes] : [];
            return heard.Length >= expected;
        }, $"the publisher to deliver {expected} events");
        return heard;
    }

    private static JsonElement Body(RealtimeEnvelope envelope) => JsonSerializer.SerializeToElement(envelope.Data);

    [Fact]
    public async Task Queue_items_added_removed_and_changed_go_to_the_queue_subject_with_what_a_row_needs_and_move_the_downloads_section()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishQueueItemAddedAsync("q1", "Big Buck Bunny", "download", "queued", CancellationToken.None);
        await _publisher.PublishQueueItemStatusChangedAsync("q1", "failed", "The client said no.", CancellationToken.None);
        await _publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);

        var heard = await HeardAsync(3, "queue");

        Assert.Equal(["QueueItemAdded", "QueueItemStatusChanged", "QueueItemRemoved"], heard.Select(item => item.Name));
        Assert.All(heard, item => Assert.Equal("queue", item.Subject));
        Assert.Equal("Big Buck Bunny", Body(heard[0]).GetProperty("title").GetString());
        Assert.Equal("queued", Body(heard[0]).GetProperty("status").GetString());
        Assert.Equal("failed", Body(heard[1]).GetProperty("status").GetString());
        Assert.Equal("The client said no.", Body(heard[1]).GetProperty("errorMessage").GetString());
        Assert.Equal("q1", Body(heard[2]).GetProperty("id").GetString());
        Assert.Equal([DashboardSectionNames.Downloads], Sections("QueueItemRemoved"));
    }

    [Fact]
    public async Task A_finished_search_goes_to_its_library_and_to_the_dashboard_because_the_schedule_lists_every_library()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishSearchRunCompletedAsync("lib-1", "Films", "movies", 5, 3, 2, "2026-10-01T12:00:00Z", CancellationToken.None);

        var heard = await HeardAsync(2, "library:lib-1", "dashboard");

        Assert.Contains(heard, item => item.Name == "SearchRunCompleted" && item.Subject == "library:lib-1");
        Assert.Contains(heard, item => item.Name == "SearchRunCompleted" && item.Subject == "dashboard");
        Assert.Equal(3, Body(heard.First(item => item.Name == "SearchRunCompleted")).GetProperty("queuedCount").GetInt32());
        Assert.Equal([DashboardSectionNames.Automation, DashboardSectionNames.Indexers], Sections("SearchRunCompleted").Order());
    }

    [Fact]
    public async Task The_five_dispatch_stages_each_go_to_the_queue_subject_with_a_timestamp()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishDispatchGrabAttemptAsync("d1", "Release", "c1", "qBittorrent", CancellationToken.None);
        await _publisher.PublishDispatchGrabCompletedAsync("d1", "Release", "c1", true, null, CancellationToken.None);
        await _publisher.PublishDispatchDetectedAsync("d1", "Release", "abc123", 1024, CancellationToken.None);
        await _publisher.PublishDispatchImportStartedAsync("d1", "Release", "movies", CancellationToken.None);
        await _publisher.PublishDispatchImportCompletedAsync("d1", "Release", true, "/films/Release.mkv", null, CancellationToken.None);

        var heard = await HeardAsync(5, "queue");

        Assert.Equal(["DispatchGrabAttempt", "DispatchGrabCompleted", "DispatchDetected", "DispatchImportStarted", "DispatchImportCompleted"], heard.Select(item => item.Name));
        Assert.All(heard, item =>
        {
            Assert.Equal("queue", item.Subject);
            Assert.True(DateTimeOffset.TryParse(Body(item).GetProperty("timestamp").GetString(), out _));
            Assert.Equal("d1", Body(item).GetProperty("dispatchId").GetString());
        });
        Assert.Equal(
            [DashboardSectionNames.Downloads, DashboardSectionNames.Imports, DashboardSectionNames.Pipeline],
            Sections("DispatchDetected").Order());
    }

    [Fact]
    public async Task A_listener_in_the_process_hears_every_event_and_one_that_throws_cannot_break_publishing()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        var heardNames = new List<string>();
        _publisher.EventPublished += name => heardNames.Add(name);
        _publisher.EventPublished += _ => throw new InvalidOperationException("a tray that broke");

        await _publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);
        await _publisher.PublishEntityChangedAsync("Movie", "m1", CancellationToken.None);

        Assert.Equal("QueueItemRemoved", heardNames[0]);
        Assert.Contains("MovieChanged", heardNames);
        var heard = await HeardAsync(2, "queue", "dashboard");
        Assert.Contains(heard, item => item.Name == "QueueItemRemoved");
        Assert.Contains(heard, item => item.Name == "MovieChanged");
    }

    [Fact]
    public async Task When_the_outbound_queue_overflows_the_oldest_events_are_dropped_and_counted_and_a_reconnecting_page_is_told_to_resync()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "deluno.realtime.events.dropped")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref dropped, value));
        listener.Start();

        // Nothing reads the queue until the publisher starts, so 1,500 pushes overflow its 1,000 slots.
        for (var index = 0; index < 1500; index++)
        {
            await _publisher.PublishQueueItemRemovedAsync($"q{index}", CancellationToken.None);
        }

        await _publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _publisher.Resume(1, ["queue"]).Status == RealtimeResumeStatus.ResyncRequired
            && _publisher.Resume(500, ["queue"]).Status == RealtimeResumeStatus.Replayed, "the publisher to deliver what was left in its queue");

        Assert.Equal(RealtimeResumeStatus.ResyncRequired, _publisher.Resume(1, ["queue"]).Status);
        var replay = _publisher.Resume(500, ["queue"]);
        Assert.Equal(RealtimeResumeStatus.Replayed, replay.Status);
        Assert.Equal(1000, replay.Envelopes.Count);
        Assert.Equal(501, replay.Envelopes.Min(item => item.Seq));
        Assert.Equal(500, dropped);
    }

    private static string[] Sections(string eventName)
        => [.. (IReadOnlyCollection<string>)typeof(SignalRRealtimeEventPublisher)
            .GetMethod("SectionsFor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [eventName])!];
}
