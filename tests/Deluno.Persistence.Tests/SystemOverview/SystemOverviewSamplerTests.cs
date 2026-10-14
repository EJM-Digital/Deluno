using System.Net;
using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.SystemReadings;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Deluno.Worker.SystemReadings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>
/// #1098: the overview is pushed as SystemOverviewChanged on the dashboard subject every two seconds,
/// only while a screen that shows it is open, and is served by GET /api/system/overview.
/// </summary>
public sealed class SystemOverviewSamplerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Watchers : IRealtimeWatchers
    {
        public int Dashboard { get; set; }
        public int Live { get; set; }
        public int Queue { get; set; }
        public int Connections { get; set; }

        public int ConnectionCount => Connections;

        public int Count(string subject) => subject switch
        {
            RealtimeGroups.Dashboard => Dashboard,
            LiveRealtimeSubject.Name => Live,
            RealtimeGroups.Queue => Queue,
            _ => 0
        };
    }

    private static (SystemOverviewSampler Sampler, SystemOverviewService Service, RecordingRealtimeEventPublisher Publisher, Watchers Watchers, SystemOverviewServiceTests.FakeFacts Facts, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(Start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var host = new HostMetricsReaderTests.FakeHost { Cpu = new CpuTimes(0, 1_000) };
        var metrics = new HostMetricsReader(host, clock);
        metrics.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));
        var facts = new SystemOverviewServiceTests.FakeFacts();
        var service = new SystemOverviewService(
            host,
            metrics,
            new ProcessUsageReader(clock, () => (TimeSpan.Zero, 1)),
            new OverviewHistory(clock),
            new NetworkChecker(new SystemOverviewServiceTests.FakeProbe(), clock, TimeSpan.FromSeconds(1)),
            facts,
            new InMemoryApiLatencyTracker(clock),
            clock);
        var publisher = new RecordingRealtimeEventPublisher();
        var watchers = new Watchers();
        var sampler = new SystemOverviewSampler(service, publisher, watchers, clock, NullLogger<SystemOverviewSampler>.Instance);
        return (sampler, service, publisher, watchers, facts, clock);
    }

    [Fact]
    public async Task While_nobody_watches_nothing_is_pushed_and_the_pace_is_ten_seconds()
    {
        var (sampler, _, publisher, _, facts, _) = Create();

        var wait = await sampler.TickAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(10), wait);
        Assert.Empty(publisher.DashboardEvents);
        Assert.Equal(0, facts.InstanceReads);
        Assert.Equal(0, facts.DriveReads);
    }

    [Fact]
    public async Task A_screen_on_the_dashboard_gets_a_whole_overview_every_two_seconds()
    {
        var (sampler, service, publisher, watchers, _, clock) = Create();
        watchers.Dashboard = 1;

        var wait = await sampler.TickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        await sampler.TickAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2), wait);
        Assert.Equal(2, publisher.DashboardEvents.Count);
        var (name, payload) = publisher.DashboardEvents[0];
        Assert.Equal("SystemOverviewChanged", name);
        Assert.Equal(SystemOverviewEventNames.Changed, name);
        var sent = Assert.IsType<SystemOverview>(payload);
        Assert.Equal("1.0.0-rc.99", sent.Instance.Version);
        Assert.NotNull(sent.History);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task A_screen_on_live_counts_as_watching_too()
    {
        var (sampler, _, publisher, watchers, _, _) = Create();
        watchers.Live = 1;

        await sampler.TickAsync(CancellationToken.None);

        Assert.Single(publisher.DashboardEvents);
    }

    [Fact]
    public async Task A_screen_that_does_not_show_the_overview_does_not_make_Deluno_push_it()
    {
        var (sampler, _, publisher, watchers, _, _) = Create();
        watchers.Queue = 3;
        watchers.Connections = 3;

        await sampler.TickAsync(CancellationToken.None);

        Assert.Empty(publisher.DashboardEvents);
    }

    [Fact]
    public async Task When_the_last_screen_leaves_the_pushing_stops()
    {
        var (sampler, _, publisher, watchers, _, clock) = Create();
        watchers.Dashboard = 1;
        await sampler.TickAsync(CancellationToken.None);
        watchers.Dashboard = 0;
        clock.Advance(TimeSpan.FromSeconds(10));

        var wait = await sampler.TickAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(10), wait);
        Assert.Single(publisher.DashboardEvents);
    }

    [Fact]
    public void Without_a_realtime_tracker_nobody_is_watching()
    {
        Assert.False(SystemOverviewSampler.IsWatched(null));
    }

    [Fact]
    public async Task The_signalr_publisher_sends_a_dashboard_event_to_the_dashboard_subject_only()
    {
        var clock = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        await using var provider = services.BuildServiceProvider();
        using var publisher = new SignalRRealtimeEventPublisher(
            provider.GetRequiredService<IHubContext<ActivityHub>>(),
            NullLogger<SignalRRealtimeEventPublisher>.Instance,
            clock);
        await publisher.StartAsync(CancellationToken.None);
        await publisher.PublishEntityChangedAsync("Widget", "w1", CancellationToken.None);
        await publisher.PublishDashboardEventAsync(SystemOverviewEventNames.Changed, new { marker = 1 }, CancellationToken.None);
        await publisher.PublishLiveEventAsync("SomethingLive", new { marker = 2 }, CancellationToken.None);

        var envelopes = new List<RealtimeEnvelope>();
        for (var attempt = 0; attempt < 200 && envelopes.Count < 2; attempt++)
        {
            envelopes = publisher.Resume(1, [RealtimeGroups.Dashboard, RealtimeGroups.Live]).Envelopes.ToList();
            await Task.Delay(10); // reason: poll interval inside a bounded wait (200 tries above)
        }

        await publisher.StopAsync(CancellationToken.None);
        var overview = Assert.Single(envelopes, envelope => envelope.Name == "SystemOverviewChanged");
        Assert.Equal(RealtimeGroups.Dashboard, overview.Subject);
        var live = Assert.Single(envelopes, envelope => envelope.Name == "SomethingLive");
        Assert.Equal(RealtimeGroups.Live, live.Subject);
    }

    [Fact]
    public async Task The_watcher_tracker_counts_connected_browsers()
    {
        var tracker = new RealtimeWatcherTracker();

        tracker.Connected();
        tracker.Connected();
        tracker.Disconnected();

        Assert.Equal(1, tracker.ConnectionCount);
        tracker.Disconnected();
        tracker.Disconnected();
        Assert.Equal(0, tracker.ConnectionCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_endpoint_serves_the_overview_as_camel_case_json_that_is_never_cached()
    {
        var (_, service, _, _, _, _) = Create();
        var builder = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(service);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapDelunoSystemOverviewEndpoints());
            }));
        using var host = builder.Build();
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/api/system/overview");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("1.0.0-rc.99", json.RootElement.GetProperty("instance").GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("drives").ValueKind);
        Assert.True(json.RootElement.GetProperty("history").TryGetProperty("netIn", out _));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("machine").GetProperty("cpuTemperatureC").ValueKind);
        await host.StopAsync();
    }
}
