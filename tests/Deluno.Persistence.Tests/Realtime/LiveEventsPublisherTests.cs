using System.Collections.Concurrent;
using Deluno.Contracts;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Deluno.Worker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1097: the server pushes changed Dashboard sections themselves (not a nudge to fetch again),
/// within a coalescing window of about 100 ms, and only builds them while someone watches.
/// </summary>
public sealed class LiveEventsPublisherTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly FakeSectionSource _sections = new();
    private readonly FakeWatchers _watchers = new() { Dashboard = 1 };
    private readonly SignalRRealtimeEventPublisher _publisher;

    public LiveEventsPublisherTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<TimeProvider>(_clock);
        _services = services.BuildServiceProvider();
        _publisher = new SignalRRealtimeEventPublisher(
            _services.GetRequiredService<IHubContext<ActivityHub>>(),
            NullLogger<SignalRRealtimeEventPublisher>.Instance,
            _clock,
            SignalRRealtimeEventPublisher.DefaultResumeWindowSize,
            _sections,
            _watchers);
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync(CancellationToken.None);
        _publisher.Dispose();
        await _services.DisposeAsync();
    }

    private sealed class FakeSectionSource : IDashboardSectionSource
    {
        private readonly ConcurrentQueue<string[]> _calls = new();

        public IReadOnlyList<string[]> Calls => _calls.ToArray();

        public Task<DashboardSectionBuild> BuildSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken)
        {
            _calls.Enqueue([.. sections.Order(StringComparer.Ordinal)]);
            return Task.FromResult(new DashboardSectionBuild(
                sections.ToDictionary(name => name, name => (object)new { name, active = 3 }),
                []));
        }
    }

    private sealed class FakeWatchers : IRealtimeWatchers
    {
        public int Dashboard { get; set; }
        public int Live { get; set; }

        public int Count(string subject) => subject switch
        {
            RealtimeGroups.Dashboard => Dashboard,
            LiveRealtimeSubject.Name => Live,
            _ => 0
        };
    }

    private async Task StartWithAnOpeningEventAsync()
    {
        await _publisher.StartAsync(CancellationToken.None);
        // Sequence 1 is the opening event, so Resume(1, ...) can read everything after it.
        await _publisher.PublishEntityChangedAsync("Widget", "w1", CancellationToken.None);
        await WaitForAsync(RealtimeGroups.Dashboard, "WidgetChanged", 1);
    }

    private async Task<IReadOnlyList<RealtimeEnvelope>> WaitForAsync(string subject, string name, int expected, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        IReadOnlyList<RealtimeEnvelope> found = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            found = _publisher.Resume(1, [subject]).Envelopes.Where(envelope => envelope.Name == name).ToArray();
            if (found.Count >= expected)
            {
                break;
            }

            await Task.Delay(5); // reason: poll interval inside a bounded wait (the deadline above)
        }

        return found;
    }

    /// <summary>Advances the fake clock in small steps until the push is seen; returns how much fake time it took.</summary>
    private async Task<TimeSpan> AdvanceUntilPushedAsync(int expected)
    {
        var advanced = TimeSpan.Zero;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_publisher.Resume(1, [RealtimeGroups.Dashboard]).Envelopes.Count(e => e.Name == LiveEventNames.DashboardSummaryUpdated) >= expected)
            {
                return advanced;
            }

            _clock.Advance(TimeSpan.FromMilliseconds(10));
            advanced += TimeSpan.FromMilliseconds(10);
            await Task.Delay(5); // reason: lets the publisher's background loop run between fake-clock steps; it exposes no signal to await; bounded by the 8 s deadline above
        }

        return advanced;
    }

    [Fact]
    public async Task A_change_pushes_the_changed_sections_themselves_within_the_coalescing_window()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishHealthChangedAsync("indexer", "problem", "Down", CancellationToken.None);
        var took = await AdvanceUntilPushedAsync(1);

        Assert.True(took < TimeSpan.FromMilliseconds(500), $"The push took {took.TotalMilliseconds} ms of clock time.");
        var push = Assert.Single(await WaitForAsync(RealtimeGroups.Dashboard, LiveEventNames.DashboardSummaryUpdated, 1));
        Assert.Equal(RealtimeGroups.Dashboard, push.Subject);
        var json = System.Text.Json.JsonSerializer.Serialize(push.Data);
        Assert.Contains("\"health\"", json);
        Assert.Contains("\"active\":3", json);
        Assert.Contains("asOfUtc", json);
    }

    [Fact]
    public async Task Nothing_is_pushed_before_the_window_ends_and_a_burst_becomes_one_push_of_the_union()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishHealthChangedAsync("indexer", "problem", "Down", CancellationToken.None);
        for (var tick = 0; tick < 60; tick++)
        {
            await _publisher.PublishDownloadProgressAsync("d1", "Film", tick, 1.5, null, "downloading", CancellationToken.None);
        }

        await _publisher.PublishActivityEventAddedAsync("a1", "Something happened", "search", "info", "2026-10-01T12:00:00Z", CancellationToken.None);
        await Task.Delay(100); // reason: the fake clock has not moved, so nothing can be pushed; this gives the loop real time to wrongly push before the assertion
        Assert.Empty(_sections.Calls);

        await AdvanceUntilPushedAsync(1);
        // Let anything further settle: the burst must not have produced a second push.
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(100); // reason: shows no second push follows; absence needs the loop given real time to wrongly push

        var call = Assert.Single(_sections.Calls);
        Assert.Equal(["activity", "downloads", "health", "indexers", "system"], call);
        Assert.Single(await WaitForAsync(RealtimeGroups.Dashboard, LiveEventNames.DashboardSummaryUpdated, 1));
    }

    [Fact]
    public async Task A_change_after_a_quiet_spell_is_pushed_again_without_waiting_out_any_gap()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishActivityEventAddedAsync("a1", "One", "search", "info", "2026-10-01T12:00:00Z", CancellationToken.None);
        await AdvanceUntilPushedAsync(1);

        await _publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);
        var took = await AdvanceUntilPushedAsync(2);

        Assert.True(took < TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, _sections.Calls.Count);
    }

    [Fact]
    public async Task Nothing_is_built_while_no_screen_watches()
    {
        _watchers.Dashboard = 0;
        _watchers.Live = 0;
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishHealthChangedAsync("indexer", "problem", "Down", CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(200); // reason: shows nothing is built with no watchers; absence needs the loop given real time to wrongly build
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100); // reason: shows nothing is built with no watchers; absence needs the loop given real time to wrongly build

        Assert.Empty(_sections.Calls);
    }

    [Fact]
    public async Task An_event_the_dashboard_does_not_read_pushes_no_sections()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishEntityChangedAsync("QualityProfile", "q1", CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(200); // reason: shows no sections are pushed for an event the dashboard does not read; absence needs the loop given real time
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(_sections.Calls);
    }

    [Fact]
    public async Task Every_pushed_live_event_goes_to_the_live_subject_with_its_data_and_the_next_sequence()
    {
        await StartWithAnOpeningEventAsync();
        var at = _clock.GetUtcNow().ToString("O");

        await _publisher.PublishJourneyStageChangedAsync(new JourneyStageChangedEvent("d1", "Dune", "movies", "movie", "m1", "l1", "grab_succeeded", null, at), CancellationToken.None);
        await _publisher.PublishProcessorHandoffChangedAsync(new ProcessorHandoffChangedEvent("h1", "q1", "l1", "Dune", "started", "Weir", null, at), CancellationToken.None);
        await _publisher.PublishRecoveryCaseChangedAsync(new RecoveryCaseChangedEvent("c1", "opened", "no_match", "Dune", "movie", null, at), CancellationToken.None);
        await _publisher.PublishJobChangedAsync(new JobChangedEvent("j1", "movies.library.search", "started", "Checking Films", "l1", null, at), CancellationToken.None);
        await _publisher.PublishSearchCycleProgressAsync(new SearchCycleProgressEvent("l1", "Films", "movies", "running", 4, 10, at), CancellationToken.None);
        await _publisher.PublishIndexerQueryRecordedAsync(new IndexerQueryRecordedEvent("i1", "Fixture", "answered", "Dune 2024", 17, 240, false, null, at), CancellationToken.None);
        await _publisher.PublishClientStateChangedAsync(new ClientStateChangedEvent("c1", "qBittorrent", false, null, "Refused the connection.", at), CancellationToken.None);
        await _publisher.PublishDriveSpaceChangedAsync(new DriveSpaceChangedEvent("/films", 1_000, 5_000, at), CancellationToken.None);
        await _publisher.PublishDownloadSpeedSampleAsync(new DownloadSpeedSampleEvent(12.5, 1.5, 2, at), CancellationToken.None);

        await WaitForAsync(LiveRealtimeSubject.Name, LiveEventNames.DownloadSpeedSample, 1);
        var live = _publisher.Resume(1, [LiveRealtimeSubject.Name]).Envelopes.OrderBy(envelope => envelope.Seq).ToArray();

        Assert.Equal(
            [
                LiveEventNames.JourneyStageChanged, LiveEventNames.ProcessorHandoffChanged, LiveEventNames.RecoveryCaseChanged,
                LiveEventNames.JobChanged, LiveEventNames.SearchCycleProgress, LiveEventNames.IndexerQueryRecorded,
                LiveEventNames.ClientStateChanged, LiveEventNames.DriveSpaceChanged, LiveEventNames.DownloadSpeedSample
            ],
            live.Select(envelope => envelope.Name).ToArray());
        Assert.All(live, envelope => Assert.Equal(LiveRealtimeSubject.Name, envelope.Subject));
        Assert.Equal(live.Length, live.Select(envelope => envelope.Seq).Distinct().Count());
        Assert.Contains(
            "\"resultCount\":17",
            System.Text.Json.JsonSerializer.Serialize(live[5].Data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task An_activity_push_carries_what_the_row_concerns_so_a_screen_can_draw_it_without_asking_again()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishActivityEventAddedAsync(
            new ActivityEventPush(
                "a9", "Imported Dune", "download.import.completed", "success", "2026-10-01T12:00:00Z",
                "movie", "m1", "j1", "Dune", "/films/Dune.mkv", "qBittorrent", null, "{\"title\":\"Dune\"}"),
            CancellationToken.None);
        var pushed = (await WaitForAsync(RealtimeGroups.Activity, "ActivityEventAdded", 1)).Single();
        var json = System.Text.Json.JsonSerializer.Serialize(pushed.Data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"relatedEntityType\":\"movie\"", json);
        Assert.Contains("\"relatedEntityId\":\"m1\"", json);
        Assert.Contains("\"relatedJobId\":\"j1\"", json);
        Assert.Contains("\"title\":\"Dune\"", json);
        Assert.Contains("\"type\":\"imported\"", json);
        Assert.Contains("\"outcome\":\"good\"", json);
        Assert.Contains("\"detailsJson\":", json);
    }

    [Fact]
    public async Task A_change_the_bell_counts_is_announced_on_the_dashboard_beside_its_live_event()
    {
        await StartWithAnOpeningEventAsync();
        var at = _clock.GetUtcNow().ToString("O");

        await _publisher.PublishRecoveryCaseChangedAsync(new RecoveryCaseChangedEvent("c1", "opened", "no_match", "Dune", "movie", null, at), CancellationToken.None);
        await _publisher.PublishConnectivityChangedAsync(new ConnectivityChangedEvent(true, at, at, "Internet down"), CancellationToken.None);
        await _publisher.PublishDownloadHealthChangedAsync("c1", CancellationToken.None);

        Assert.Single(await WaitForAsync(RealtimeGroups.Dashboard, LiveEventNames.AttentionChanged, 1));
        Assert.Single(await WaitForAsync(RealtimeGroups.Dashboard, LiveEventNames.ConnectivityChanged, 1));
        Assert.Single(await WaitForAsync(RealtimeGroups.Dashboard, LiveEventNames.DownloadHealthChanged, 1));
        Assert.Single(await WaitForAsync(LiveRealtimeSubject.Name, LiveEventNames.RecoveryCaseChanged, 1));
    }

    [Fact]
    public void Watching_makes_the_clients_asked_every_second_only_while_something_is_downloading()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), DownloadProgressPublisher.NextInterval(moving: true, watched: true));
        Assert.Equal(TimeSpan.FromSeconds(2), DownloadProgressPublisher.NextInterval(moving: true, watched: false));
        Assert.Equal(TimeSpan.FromSeconds(20), DownloadProgressPublisher.NextInterval(moving: false, watched: true));
        Assert.Equal(TimeSpan.FromSeconds(20), DownloadProgressPublisher.NextInterval(moving: false, watched: false));
    }

    [Fact]
    public void The_refresh_loop_uses_the_same_watched_rule()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), Deluno.Integrations.DownloadClients.DownloadTelemetryRefreshService.NextInterval(moving: true, watched: true));
        Assert.Equal(TimeSpan.FromSeconds(2), Deluno.Integrations.DownloadClients.DownloadTelemetryRefreshService.NextInterval(moving: true, watched: false));
        Assert.Equal(TimeSpan.FromSeconds(20), Deluno.Integrations.DownloadClients.DownloadTelemetryRefreshService.NextInterval(moving: false, watched: true));
    }
}

/// <summary>#1097: the server knows when a screen is watching Live, and stops knowing the moment it leaves or drops.</summary>
public sealed class RealtimeWatcherTests
{
    private static async Task<(IHost Host, TestServer Server, RealtimeWatcherTracker Tracker)> StartAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(TimeProvider.System);
                    services.AddSignalR();
                    services.AddSingleton<RealtimeWatcherTracker>();
                    services.AddSingleton(provider => new SignalRRealtimeEventPublisher(
                        provider.GetRequiredService<IHubContext<ActivityHub>>(),
                        NullLogger<SignalRRealtimeEventPublisher>.Instance,
                        provider.GetRequiredService<TimeProvider>()));
                    services.AddSingleton<IRealtimeResumeSource>(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                    services.AddHostedService(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoRealtime());
                }))
            .Build();
        await host.StartAsync();
        var server = host.GetTestServer();
        return (host, server, server.Services.GetRequiredService<RealtimeWatcherTracker>());
    }

    private static HubConnection Connect(TestServer server) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/deluno"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(10); // reason: poll interval inside a bounded wait (5 s above)
        }
    }

    [Fact]
    public async Task Joining_and_leaving_the_live_subject_counts_watchers_and_a_dropped_connection_stops_watching()
    {
        var (host, server, tracker) = await StartAsync();
        try
        {
            Assert.Equal(0, tracker.Count(LiveRealtimeSubject.Name));

            await using var first = Connect(server);
            await first.StartAsync();
            await first.InvokeAsync("Subscribe", new[] { LiveRealtimeSubject.Name, RealtimeGroups.Queue });
            await using var second = Connect(server);
            await second.StartAsync();
            await second.InvokeAsync("Subscribe", new[] { LiveRealtimeSubject.Name });
            // Joining the same subject twice from one connection is still one watcher.
            await second.InvokeAsync("Subscribe", new[] { LiveRealtimeSubject.Name });

            Assert.Equal(2, tracker.Count(LiveRealtimeSubject.Name));
            Assert.Equal(1, tracker.Count(RealtimeGroups.Queue));

            await first.InvokeAsync("Unsubscribe", new[] { LiveRealtimeSubject.Name });
            Assert.Equal(1, tracker.Count(LiveRealtimeSubject.Name));

            await second.StopAsync();
            await WaitUntilAsync(() => tracker.Count(LiveRealtimeSubject.Name) == 0);
            Assert.Equal(0, tracker.Count(LiveRealtimeSubject.Name));

            await first.StopAsync();
            await WaitUntilAsync(() => tracker.Count(RealtimeGroups.Queue) == 0);
            Assert.Equal(0, tracker.Count(RealtimeGroups.Queue));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task A_reconnect_replays_the_live_events_it_missed_once_each_in_order()
    {
        var (host, server, _) = await StartAsync();
        try
        {
            var publisher = server.Services.GetRequiredService<SignalRRealtimeEventPublisher>();
            var at = DateTimeOffset.UtcNow.ToString("O");
            var seen = new ConcurrentQueue<RealtimeEnvelope>();

            await using var first = Connect(server);
            first.On<RealtimeEnvelope>("RealtimeEvent", envelope => seen.Enqueue(envelope));
            await first.StartAsync();
            await first.InvokeAsync("Subscribe", new[] { LiveRealtimeSubject.Name });
            await publisher.PublishJobChangedAsync(new JobChangedEvent("j1", "movies.library.search", "started", "Checking Films", "l1", null, at), CancellationToken.None);
            await WaitUntilAsync(() => seen.Count >= 1);
            var lastSeq = seen.Max(envelope => envelope.Seq);
            await first.StopAsync();

            // These happen while nobody is listening.
            await publisher.PublishJobChangedAsync(new JobChangedEvent("j1", "movies.library.search", "finished", "Checking Films", "l1", null, at), CancellationToken.None);
            await publisher.PublishDownloadSpeedSampleAsync(new DownloadSpeedSampleEvent(5, 1, 1, at), CancellationToken.None);
            await WaitUntilAsync(() => publisher.Resume(lastSeq, [LiveRealtimeSubject.Name]).Envelopes.Count >= 2);

            await using var second = Connect(server);
            await second.StartAsync();
            await second.InvokeAsync("Subscribe", new[] { LiveRealtimeSubject.Name });
            var result = await second.InvokeAsync<RealtimeResumeResult>("Resume", lastSeq, new[] { LiveRealtimeSubject.Name });

            Assert.Equal(RealtimeResumeStatus.Replayed, result.Status);
            Assert.Equal(
                [LiveEventNames.JobChanged, LiveEventNames.DownloadSpeedSample],
                result.Envelopes.Select(envelope => envelope.Name).ToArray());
            Assert.All(result.Envelopes, envelope => Assert.True(envelope.Seq > lastSeq));
            Assert.Equal(result.Envelopes.Count, result.Envelopes.Select(envelope => envelope.Seq).Distinct().Count());

            // Asking again from the newest sequence replays nothing: no double counting.
            var again = await second.InvokeAsync<RealtimeResumeResult>("Resume", result.Envelopes.Max(envelope => envelope.Seq), new[] { LiveRealtimeSubject.Name });
            Assert.Equal(RealtimeResumeStatus.CaughtUp, again.Status);
            Assert.Empty(again.Envelopes);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
