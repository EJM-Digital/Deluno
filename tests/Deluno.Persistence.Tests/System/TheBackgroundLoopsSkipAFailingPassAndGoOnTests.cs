using Deluno.Api.Monitoring;
using Deluno.Api.SystemInfo;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.SystemReadings;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Persistence.Tests.SystemReadings;
using Deluno.Platform;
using Deluno.Worker.SystemReadings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.BackgroundLoops;

/// <summary>
/// Coverage register (#1329), background workers inventory: the System screen's loops are driven by a fake clock. A pass that
/// fails is skipped with a debug line (or, for the hourly recorder, a warning) and the loop carries on at its own pace: the
/// watcher every 15 s, the throttle watcher every 2 s, the sampler retries after 2 s, and the recorder starts 30 s after the start
/// and then goes hourly.
/// </summary>
public sealed class TheBackgroundLoopsSkipAFailingPassAndGoOnTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) { Lines.Add((logLevel, formatter(state, exception))); }
        }

        public int Count(LogLevel level, string contains)
        {
            lock (Lines) { return Lines.Count(line => line.Level == level && line.Message.Contains(contains, StringComparison.Ordinal)); }
        }
    }

    private sealed class Watching : IRealtimeWatchers
    {
        public int Count(string subject) => subject == "dashboard" ? 1 : 0;
    }

    // ---- SystemChangeWatcher

    private sealed class FlakyMonitoring : IMonitoringService
    {
        public int Reads;

        public Task<MonitoringDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Reads) == 1)
            {
                throw new InvalidOperationException("monitoring was busy");
            }

            return Task.FromResult(new MonitoringDashboardSnapshot(
                Start,
                new MonitoringReadinessSummary("ready", true, 5, 0),
                new MonitoringStorageSummary("data", 1000, 500, 50, false),
                new MonitoringServiceSummary(1, 1, 1, 1, 0, 0, 0, 0),
                new MonitoringPerformanceSummary(0, null, null, null, new ApiLatencySnapshot(Start, Start, 0, 0, 0, 0, 0)),
                []));
        }

        public Task<IReadOnlyList<MonitoringAlertItem>> GetAlertsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Deluno.Contracts.Page<MonitoringDiagnosticItem>> SearchDiagnosticsAsync(MonitoringDiagnosticsQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MonitoringExportSnapshot> BuildExportSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_system_change_watcher_skips_a_failing_look_with_a_debug_line_and_looks_again_fifteen_seconds_later()
    {
        var dataRoot = Directory.CreateTempSubdirectory("deluno-loop-watcher-").FullName;
        var clock = new FakeTimeProvider(Start);
        var monitoring = new FlakyMonitoring();
        var logger = new ListLogger<SystemChangeWatcher>();
        var watcher = new SystemChangeWatcher(Options.Create(new StoragePathOptions { DataRoot = dataRoot }), monitoring, new NullRealtimeEventPublisher(), new Watching(), clock, logger);
        try
        {
            await watcher.StartAsync(CancellationToken.None);

            // Advance in whole intervals until the loop has made its second look; a look that fails must not end the loop.
            await Wait.UntilAsync(() => { clock.Advance(SystemChangeWatcher.Interval); return Volatile.Read(ref monitoring.Reads) >= 2; }, "the watcher's second look after a failed first one");

            Assert.Equal(TimeSpan.FromSeconds(15), SystemChangeWatcher.Interval);
            Assert.Equal(1, logger.Count(LogLevel.Debug, "A System change check was skipped."));
            Assert.False(watcher.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    // ---- OutboundThrottleWatcher

    private sealed class FlakyThrottle : IOutboundRequestThrottle
    {
        public int Describes;

        public ValueTask<TimeSpan?> TryAcquireAsync(string host, OutboundRate rate, TimeSpan maxWait, CancellationToken cancellationToken)
            => ValueTask.FromResult<TimeSpan?>(TimeSpan.Zero);

        public IReadOnlyList<OutboundThrottleHostState> Describe()
        {
            if (Interlocked.Increment(ref Describes) == 1)
            {
                throw new InvalidOperationException("throttle unreadable");
            }

            return [];
        }
    }

    [Fact]
    public async Task The_throttle_watcher_skips_a_failing_look_and_looks_again_two_seconds_later()
    {
        var clock = new FakeTimeProvider(Start);
        var throttle = new FlakyThrottle();
        var logger = new ListLogger<OutboundThrottleWatcher>();
        var watcher = new OutboundThrottleWatcher(throttle, new NullRealtimeEventPublisher(), new Watching(), clock, logger);
        try
        {
            await watcher.StartAsync(CancellationToken.None);

            await Wait.UntilAsync(() => { clock.Advance(OutboundThrottleWatcher.Interval); return Volatile.Read(ref throttle.Describes) >= 2; }, "the throttle watcher's second look after a failed first one");

            Assert.Equal(TimeSpan.FromSeconds(2), OutboundThrottleWatcher.Interval);
            Assert.False(watcher.ExecuteTask!.IsCompleted);
            Assert.Equal(1, logger.Count(LogLevel.Debug, "skipped"));
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    // ---- SystemOverviewSampler

    private sealed class FlakyPublisher : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        public int Calls;
        public int Delivered;

        public Task PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                throw new InvalidOperationException("the push failed");
            }

            Interlocked.Increment(ref Delivered);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_sampler_treats_a_failing_reading_as_a_gap_with_a_debug_line_and_tries_again_after_two_seconds()
    {
        var clock = new FakeTimeProvider(Start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var host = new HostMetricsReaderTests.FakeHost { Cpu = new Deluno.Infrastructure.SystemReadings.CpuTimes(0, 1_000) };
        var metrics = new Deluno.Infrastructure.SystemReadings.HostMetricsReader(host, clock);
        metrics.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));
        var service = new SystemOverviewService(
            host,
            metrics,
            new Deluno.Infrastructure.SystemReadings.ProcessUsageReader(clock, () => (TimeSpan.Zero, 1)),
            new OverviewHistory(clock),
            new NetworkChecker(new SystemOverviewServiceTests.FakeProbe(), clock, TimeSpan.FromSeconds(1)),
            new SystemOverviewServiceTests.FakeFacts(),
            new InMemoryApiLatencyTracker(clock),
            clock);
        var publisher = new FlakyPublisher();
        var logger = new ListLogger<SystemOverviewSampler>();
        var sampler = new SystemOverviewSampler(service, publisher, new Watching(), clock, logger);
        try
        {
            await sampler.StartAsync(CancellationToken.None);

            await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromSeconds(2)); return Volatile.Read(ref publisher.Delivered) >= 1; }, "the sampler's first delivered overview after a failed push");

            Assert.Equal(1, logger.Count(LogLevel.Debug, "A System overview reading was skipped."));
            Assert.False(sampler.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    // ---- SystemOverviewRecorder

    private sealed class RecordingStore : ISystemOverviewStore
    {
        public int Starts;
        public int DriveWrites;
        public int Prunes;
        public int FailNextDriveWrites;
        public DateTimeOffset? LastPruneCutoff;

        public Task RecordDriveSpaceAsync(string volumeKey, DateOnly day, long freeBytes, long totalBytes, CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref FailNextDriveWrites) >= 0)
            {
                throw new InvalidOperationException("the database was busy");
            }

            Interlocked.Increment(ref DriveWrites);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DriveSpaceDay>> ListDriveSpaceAsync(DateOnly since, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordStartAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            return Task.CompletedTask;
        }

        public Task<int> CountStartsSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        {
            LastPruneCutoff = olderThanUtc;
            Interlocked.Increment(ref Prunes);
            return Task.CompletedTask;
        }

        public Task<JobDayStats> GetJobDayStatsAsync(DateTimeOffset sinceUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_recorder_records_the_start_at_once_the_drive_space_thirty_seconds_later_then_hourly_and_a_failed_hour_is_warned_and_retried_the_next()
    {
        var clock = new FakeTimeProvider(Start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var store = new RecordingStore { FailNextDriveWrites = 1 };
        var facts = new SystemOverviewServiceTests.FakeFacts { Drives = [new DriveFacts("c:", "C:", "C:\\", 1000, 500, 10, null)] };
        var services = new ServiceCollection().AddSingleton<ISystemOverviewStore>(store).BuildServiceProvider();
        var logger = new ListLogger<SystemOverviewRecorder>();
        var recorder = new SystemOverviewRecorder(services.GetRequiredService<IServiceScopeFactory>(), facts, clock, logger);
        try
        {
            await recorder.StartAsync(CancellationToken.None);
            await Wait.UntilAsync(() => Volatile.Read(ref store.Starts) == 1, "the start being recorded");

            Assert.Equal(0, facts.DriveReads);
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(0, facts.DriveReads);

            await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromSeconds(1)); return facts.DriveReads >= 1; }, "the first drive reading thirty seconds after the start");
            Assert.Equal(0, store.DriveWrites);
            await Wait.UntilAsync(() => logger.Count(LogLevel.Warning, "could not record the drive space this hour") == 1, "the failed hour being warned about");

            await Wait.UntilAsync(() => { clock.Advance(SystemOverviewRecorder.Interval); return Volatile.Read(ref store.DriveWrites) >= 1; }, "the next hour's reading being stored");

            Assert.Equal(TimeSpan.FromHours(1), SystemOverviewRecorder.Interval);
            Assert.Equal(1, store.Starts);
            Assert.True(store.Prunes >= 1);
            Assert.True(clock.GetUtcNow() - store.LastPruneCutoff!.Value >= TimeSpan.FromDays(14));
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }
}
