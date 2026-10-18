using System.Reflection;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.RealDisk;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Worker.HealthChecks.Machine;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Services;

/// <summary>
/// The loops that wake on a clock: keep-awake, the download-client refresher, and the gateway that hands the sharing rule a
/// fresh scope for each reclaim. The clock is a fake one, moved in small steps because each service runs on its own thread.
/// </summary>
public sealed class TheBackgroundLoopsTests
{
    // ---------------------------------------------------------------- KeepAwakeService

    [Fact]
    public async Task Keep_awake_reads_its_setting_at_start_and_then_on_every_thirty_second_tick_and_not_before()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // reason: the service only runs on Windows; the next test shows it does nothing elsewhere
        }

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
        var settings = Settings.Create(keepAwake: false);
        var service = new KeepAwakeService(settings.Proxy, NullLogger<KeepAwakeService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => settings.Reads == 1, "the setting to be read as the service starts");
            clock.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(100); // reason: shows nothing happened before the tick; there is no event for an absence
            Assert.Equal(1, settings.Reads);

            Assert.Equal(KeepAwakeService.Interval, TimeSpan.FromSeconds(30));
            Assert.True(await AdvanceUntilAsync(clock, () => settings.Reads >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), "the tick at 30 seconds reads the setting again, so switching it on takes hold within half a minute");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Keep_awake_stops_at_once_when_Deluno_stops_and_a_setting_that_cannot_be_read_does_not_end_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            var elsewhere = Settings.Create(keepAwake: true);
            var idle = new KeepAwakeService(elsewhere.Proxy, NullLogger<KeepAwakeService>.Instance, new FakeTimeProvider());
            await idle.StartAsync(CancellationToken.None);
            await idle.StopAsync(CancellationToken.None);
            Assert.Equal(0, elsewhere.Reads);
            return;
        }

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
        var settings = Settings.Create(keepAwake: true, failFirstRead: true);
        var service = new KeepAwakeService(settings.Proxy, NullLogger<KeepAwakeService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100); // reason: lets the service reach its first wait; there is no event for it
        Assert.True(await AdvanceUntilAsync(clock, () => settings.Reads >= 2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2)), "a failed read is skipped and the next tick reads again");
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted, "the loop ends when Deluno stops");
    }

    // ---------------------------------------------------------------- DownloadTelemetryRefreshService

    [Fact]
    public async Task The_download_client_snapshot_is_refreshed_two_seconds_after_start_then_every_twenty_while_nothing_moves_each_time_as_a_new_reading()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
        var store = ScriptedSnapshots.Create(failFirst: 0);
        var service = new DownloadTelemetryRefreshService(store.Proxy, clock, NullLogger<DownloadTelemetryRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100); // reason: lets the service reach its first wait; there is no event for it
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100); // reason: shows no reading before the two seconds are up
            Assert.Equal(0, store.Refreshes);

            Assert.True(await AdvanceUntilAsync(clock, () => store.Refreshes == 1, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)), "the first reading comes about two seconds after start");
            clock.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(100); // reason: shows no second reading before twenty seconds
            Assert.Equal(1, store.Refreshes);
            Assert.True(await AdvanceUntilAsync(clock, () => store.Refreshes == 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)), "with nothing moving the next reading is twenty seconds on");
            Assert.All(store.ReuseWithin, reuse => Assert.True(reuse < TimeSpan.Zero, "a refresh never reuses an older reading"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_refresh_is_warned_once_the_loop_goes_on_and_the_last_reading_stays()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
        var store = ScriptedSnapshots.Create(failFirst: 2);
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<DownloadTelemetryRefreshService>();
        var service = new DownloadTelemetryRefreshService(store.Proxy, clock, logger);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100); // reason: lets the service reach its first wait; there is no event for it
            Assert.True(await AdvanceUntilAsync(clock, () => store.Refreshes >= 3, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2)), "after two failed readings the loop reads a third time");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("skipped"));
    }

    // ---------------------------------------------------------------- DownloadClientReclaimGateway

    [Fact]
    public async Task Each_reclaim_and_each_stop_seeding_runs_in_a_scope_of_its_own_and_reports_what_the_client_said()
    {
        var instances = new List<Guid>();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var proxy = DispatchProxy.Create<IDownloadClientTelemetryService, ScopeRecordingTelemetry>();
            var recorder = (ScopeRecordingTelemetry)(object)proxy;
            lock (instances) { instances.Add(recorder.Id); }
            return proxy;
        });
        await using var provider = services.BuildServiceProvider();
        var gateway = new DownloadClientReclaimGateway(provider.GetRequiredService<IServiceScopeFactory>());

        var first = await gateway.RemoveWithDataAsync("client-1", "item-1", CancellationToken.None);
        var second = await gateway.RemoveWithDataAsync("client-1", "item-2", CancellationToken.None);
        var paused = await gateway.StopSeedingAsync("client-1", "item-3", CancellationToken.None);

        Assert.Equal(3, instances.Distinct().Count());
        Assert.True(first.Succeeded);
        Assert.Equal("reclaim:item-1", first.Message);
        Assert.Equal("reclaim:item-2", second.Message);
        Assert.True(paused.Succeeded);
        Assert.Equal("pause:item-3", paused.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> done, TimeSpan step, TimeSpan limit)
    {
        for (var moved = TimeSpan.Zero; moved < limit; moved += step)
        {
            if (done()) return true;
            clock.Advance(step);
            await Task.Delay(25); // reason: lets the service's own thread react to each fake-clock step; it has no signal to await, and the loop is bounded by limit
        }

        return done();
    }

    private class Settings : DispatchProxy
    {
        private int _reads;
        private bool _keepAwake;
        private bool _failFirstRead;

        public int Reads => Volatile.Read(ref _reads);

        public IPlatformSettingsRepository Proxy => (IPlatformSettingsRepository)(object)this;

        public static Settings Create(bool keepAwake, bool failFirstRead = false)
        {
            var settings = (Settings)(object)DispatchProxy.Create<IPlatformSettingsRepository, Settings>();
            settings._keepAwake = keepAwake;
            settings._failFirstRead = failFirstRead;
            return settings;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPlatformSettingsRepository.GetAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var read = Interlocked.Increment(ref _reads);
            if (_failFirstRead && read == 1)
            {
                throw new IOException("the settings file is being written");
            }

            return Task.FromResult(FakePlatformSettingsRepository.Default() with { KeepComputerAwake = _keepAwake });
        }
    }

    private class ScriptedSnapshots : DispatchProxy
    {
        private int _refreshes;
        private int _failFirst;
        private readonly List<TimeSpan> _reuse = [];

        public int Refreshes => Volatile.Read(ref _refreshes);

        public IReadOnlyList<TimeSpan> ReuseWithin { get { lock (_reuse) { return [.. _reuse]; } } }

        public IDownloadTelemetrySnapshotStore Proxy => (IDownloadTelemetrySnapshotStore)(object)this;

        public static ScriptedSnapshots Create(int failFirst)
        {
            var store = (ScriptedSnapshots)(object)DispatchProxy.Create<IDownloadTelemetrySnapshotStore, ScriptedSnapshots>();
            store._failFirst = failFirst;
            return store;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDownloadTelemetrySnapshotStore.RefreshAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var count = Interlocked.Increment(ref _refreshes);
            lock (_reuse) { _reuse.Add((TimeSpan)args![0]!); }
            if (count <= _failFirst)
            {
                throw new HttpRequestException("the client did not answer");
            }

            var now = DateTimeOffset.UtcNow;
            var nothing = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
            return Task.FromResult(new SnapshotEntry<DownloadTelemetryCapture>(new DownloadTelemetryCapture(new DownloadTelemetryOverview(nothing, [], now), []), now));
        }
    }

    private class ScopeRecordingTelemetry : DispatchProxy
    {
        public Guid Id { get; } = Guid.NewGuid();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IDownloadClientTelemetryService.ReclaimCompletedAsync):
                    return Task.FromResult(new DownloadClientActionResult((string)args![0]!, (string)args[1]!, "reclaim", true, $"reclaim:{args[1]}"));
                case nameof(IDownloadClientTelemetryService.ExecuteActionAsync):
                    var request = (DownloadClientActionRequest)args![1]!;
                    Assert.Equal(DownloadClientActions.Pause, request.Action);
                    return Task.FromResult(new DownloadClientActionResult((string)args[0]!, request.QueueItemId, request.Action, true, $"pause:{request.QueueItemId}"));
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
