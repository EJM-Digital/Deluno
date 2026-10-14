using System.Reflection;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1097: while a screen watches Live and something downloads, the clients are read every second and
/// each reading pushes its progress and a speed sample; with nobody watching nothing changes.
/// </summary>
public sealed class LiveDownloadReadingsTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly Watchers _watchers = new();
    private readonly ServiceProvider _services;
    private readonly ReadingProxy _readings;
    private readonly DownloadProgressPublisher _publisher;

    public LiveDownloadReadingsTests()
    {
        _readings = DispatchProxy.Create<IDownloadClientTelemetryService, ReadingProxy>() is ReadingProxy proxy
            ? proxy
            : throw new InvalidOperationException();
        var services = new ServiceCollection();
        services.AddSingleton<IDownloadClientTelemetryService>((IDownloadClientTelemetryService)(object)_readings);
        _services = services.BuildServiceProvider();
        _publisher = new DownloadProgressPublisher(
            NullLogger<DownloadProgressPublisher>.Instance,
            _services.GetRequiredService<IServiceScopeFactory>(),
            _realtime,
            _clock,
            _watchers);
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync(CancellationToken.None);
        _publisher.Dispose();
        await _services.DisposeAsync();
    }

    private class ReadingProxy : DispatchProxy
    {
        public Func<DownloadTelemetryOverview> Overview { get; set; } = () => throw new InvalidOperationException("No reading set.");

        public int Readings { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDownloadClientTelemetryService.GetFreshOverviewAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Readings++;
            return Task.FromResult(Overview());
        }
    }

    private sealed class Watchers : IRealtimeWatchers
    {
        public int Live { get; set; }

        public int Count(string subject) => subject == LiveRealtimeSubject.Name ? Live : 0;
    }

    private DownloadTelemetryOverview Reading(double progress, double speed, string? failure = null, bool? paused = null)
    {
        var item = new DownloadQueueItem(
            "item-1", "client-1", "qBittorrent", "torrent", "movies", "Dune", "Dune.2024", "movies",
            DownloadQueueStatuses.Downloading, progress, speed, 600, 1_000, 100, 4, "Fixture", null, _clock.GetUtcNow());
        var snapshot = new DownloadClientTelemetrySnapshot(
            "client-1", "qBittorrent", "torrent", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(1, 0, 0, 0, 0, 0, speed, 0, 1.5),
            [item], [], _clock.GetUtcNow())
        {
            LastFailure = failure is null ? null : IntegrationFailureFactory.FromLegacy("downloadClient", "client-1", "qBittorrent", "queue", "unreachable", failure),
            ClientPaused = paused
        };
        return new DownloadTelemetryOverview(snapshot.Summary, [snapshot], _clock.GetUtcNow());
    }

    private async Task AdvanceAsync(TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var advanced = TimeSpan.Zero; advanced < total; advanced += step)
        {
            _clock.Advance(step);
            await Task.Delay(8); // reason: lets the publisher's background loop run between fake-clock steps; it exposes no signal to await
        }
    }

    [Fact]
    public async Task Watched_and_downloading_each_second_brings_a_reading_with_a_speed_sample()
    {
        _watchers.Live = 1;
        var speed = 5.0;
        _readings.Overview = () => Reading(10, speed += 1);
        await _publisher.StartAsync(CancellationToken.None);

        // The first look comes after the idle wait; once something is moving, the next ones are a second apart.
        await AdvanceAsync(TimeSpan.FromSeconds(21));
        var afterFirst = _readings.Readings;
        await AdvanceAsync(TimeSpan.FromSeconds(3));

        Assert.True(_readings.Readings - afterFirst >= 2, $"Expected one reading a second, got {_readings.Readings - afterFirst} in 3 s.");
        var samples = _realtime.LiveEventsOf<DownloadSpeedSampleEvent>(LiveEventNames.DownloadSpeedSample);
        Assert.True(samples.Count >= 3);
        Assert.Equal(1.5, samples[0].UploadMbps);
        Assert.Equal(1, samples[0].Active);
        Assert.All(_realtime.DownloadProgressIds, id => Assert.Equal("item-1", id));
    }

    [Fact]
    public async Task Nobody_watching_keeps_the_ordinary_pace_and_sends_no_speed_samples()
    {
        _watchers.Live = 0;
        _readings.Overview = () => Reading(10, 6);
        await _publisher.StartAsync(CancellationToken.None);

        await AdvanceAsync(TimeSpan.FromSeconds(21));
        var afterFirst = _readings.Readings;
        await AdvanceAsync(TimeSpan.FromSeconds(3));

        // Every two seconds, not every second: one or two readings in three seconds.
        Assert.InRange(_readings.Readings - afterFirst, 1, 2);
        Assert.Empty(_realtime.LiveEventsOf<DownloadSpeedSampleEvent>(LiveEventNames.DownloadSpeedSample));
    }

    [Fact]
    public async Task A_client_going_unreachable_or_pausing_is_pushed_the_moment_it_is_seen_and_not_before()
    {
        var reading = Reading(10, 5);
        _readings.Overview = () => reading;
        await _publisher.StartAsync(CancellationToken.None);

        await AdvanceAsync(TimeSpan.FromSeconds(21));
        // The first reading only sets what is normal.
        Assert.Empty(_realtime.LiveEventsOf<ClientStateChangedEvent>(LiveEventNames.ClientStateChanged));

        reading = Reading(10, 0, failure: "Refused the connection.");
        await AdvanceAsync(TimeSpan.FromSeconds(3));

        var change = Assert.Single(_realtime.LiveEventsOf<ClientStateChangedEvent>(LiveEventNames.ClientStateChanged));
        Assert.Equal("client-1", change.ClientId);
        Assert.Equal("qBittorrent", change.ClientName);
        Assert.False(change.Reachable);
        Assert.Equal("Refused the connection.", change.Message);
    }
}
