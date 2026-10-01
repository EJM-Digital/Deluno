using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Worker.SystemReadings;

/// <summary>
/// Builds the System overview (#1098) from real readings: the fast ones (load, memory, disk and network
/// speed) every time it is asked, and the slow ones (a name lookup, WMI, a folder size, the stored
/// records) from their own caches, so a reading never waits on them. Only reads; nothing here changes
/// anything.
///
/// <para>One instance serves the whole process. Rates are measured between two calls, so everything
/// that wants an overview goes through <see cref="SampleAsync"/>, which takes a fresh reading at most
/// about once a second and otherwise returns the last one.</para>
/// </summary>
public sealed class SystemOverviewService
{
    /// <summary>How long a built overview is served as it is.</summary>
    public static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan FastestResample = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FirstReadLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(15);

    private readonly IHostReadingSource _host;
    private readonly HostMetricsReader _metrics;
    private readonly ProcessUsageReader _process;
    private readonly OverviewHistory _history;
    private readonly IApiLatencyTracker _latency;
    private readonly TimeProvider _time;

    private readonly SlowValue<InstanceFacts?> _instance;
    private readonly SlowValue<IReadOnlyList<DriveFacts>> _drives;
    private readonly SlowValue<NetworkCheckResult?> _network;
    private readonly SlowValue<MachineFacts> _machineFacts;
    private readonly ConcurrentDictionary<string, SlowValue<DriveHealthReading>> _driveHealth = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _sampling = new(1, 1);
    private SystemOverview? _latest;
    private DateTimeOffset _latestAt;

    public SystemOverviewService(
        IHostReadingSource host,
        HostMetricsReader metrics,
        ProcessUsageReader process,
        OverviewHistory history,
        NetworkChecker network,
        ISystemOverviewFacts facts,
        IApiLatencyTracker latency,
        TimeProvider timeProvider)
    {
        _host = host;
        _metrics = metrics;
        _process = process;
        _history = history;
        _latency = latency;
        _time = timeProvider;

        _instance = new SlowValue<InstanceFacts?>(TimeSpan.FromSeconds(5), async token => await facts.ReadInstanceAsync(token), timeProvider, null);
        _drives = new SlowValue<IReadOnlyList<DriveFacts>>(TimeSpan.FromSeconds(30), facts.ReadDrivesAsync, timeProvider, []);
        _machineFacts = new SlowValue<MachineFacts>(
            TimeSpan.FromMinutes(1),
            _ => Task.FromResult(new MachineFacts(host.ReadOperatingSystem()?.Describe(), host.ReadRebootPending(), host.ReadCpuTemperatureC())),
            timeProvider,
            new MachineFacts(null, null, null));
        _network = new SlowValue<NetworkCheckResult?>(
            TimeSpan.FromSeconds(30),
            async token =>
            {
                var instance = _instance.Current;
                return await network.RunAsync(instance?.NasHost, instance?.MetadataUrl, token);
            },
            timeProvider,
            null);
    }

    /// <summary>The newest overview, rebuilt when it is more than two seconds old.</summary>
    public async Task<SystemOverview> GetAsync(CancellationToken cancellationToken)
    {
        var latest = _latest;
        if (latest is not null && _time.GetUtcNow() - _latestAt < CacheLife)
        {
            return latest;
        }

        return await SampleAsync(cancellationToken);
    }

    /// <summary>Takes a reading now (at most about once a second), builds the overview from it and keeps it.</summary>
    public async Task<SystemOverview> SampleAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_latest is not null && now - _latestAt < FastestResample)
            {
                return _latest;
            }

            var instance = await _instance.GetAsync(FirstReadLimit, cancellationToken);
            var drives = await _drives.GetAsync(FirstReadLimit, cancellationToken);

            // Rates need two points. The very first reading has none, so take a second a moment later.
            var metrics = _metrics.Read(drives.Select(drive => drive.Path).ToArray());
            if (!metrics.HasBaseline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100), _time, cancellationToken);
                metrics = _metrics.Read(drives.Select(drive => drive.Path).ToArray());
            }

            _history.Offer(metrics.CpuPercent, metrics.MemoryPercent, metrics.DiskBusyPercent, metrics.NetInBytesPerSec);

            var overview = Build(
                instance,
                drives,
                metrics,
                _process.Read(),
                await _machineFacts.GetAsync(FirstReadLimit, cancellationToken),
                _network.Current);
            _latest = overview;
            _latestAt = _time.GetUtcNow();
            return overview;
        }
        finally
        {
            _sampling.Release();
        }
    }

    /// <summary>
    /// Keeps the ten-minute charts filling while nobody has the screen open: one cheap reading of load,
    /// memory, disk and network, no more. Nothing else is read, so an unwatched Deluno costs next to nothing.
    /// </summary>
    public async Task SampleHistoryOnlyAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken);
        try
        {
            var metrics = _metrics.Read([]);
            if (metrics.HasBaseline)
            {
                _history.Offer(metrics.CpuPercent, metrics.MemoryPercent, metrics.DiskBusyPercent, metrics.NetInBytesPerSec);
            }
        }
        finally
        {
            _sampling.Release();
        }
    }

    private SystemOverview Build(
        InstanceFacts? instance,
        IReadOnlyList<DriveFacts> drives,
        HostMetrics metrics,
        ProcessUsage process,
        MachineFacts machine,
        NetworkCheckResult? network)
    {
        var answers = _latency.GetAnswerTimes(AnswerWindow);
        var day = _latency.GetDayTotals();
        var history = _history.Snapshot();

        return new SystemOverview(
            Instance: new SystemOverviewInstance(
                Version: instance?.Version ?? string.Empty,
                Latest: instance?.Latest ?? true,
                Channel: instance?.Channel ?? string.Empty,
                StartedUtc: instance?.StartedUtc ?? _time.GetUtcNow(),
                Address: instance?.Address ?? string.Empty,
                RunsAs: instance?.RunsAs ?? "app",
                CpuPercent: process.CpuPercent,
                MemoryBytes: process.MemoryBytes,
                WorkersBusy: instance?.WorkersBusy ?? 0,
                OldestWaitingSeconds: instance?.OldestWaitingSeconds,
                AnswerMs: answers.TypicalMs,
                AnswerSlowestMs: answers.SlowestFivePercentMs,
                JobsToday: instance?.JobsToday ?? 0,
                JobsFailedToday: instance?.JobsFailedToday ?? 0,
                RequestsToday: day.Requests,
                RequestErrorsToday: day.Errors,
                DataBytes: instance?.DataBytes ?? 0,
                DatabaseCount: instance?.DatabaseCount ?? 0,
                LastBackupUtc: instance?.LastBackupUtc,
                LastBackupBytes: instance?.LastBackupBytes,
                RestartsThisWeek: instance?.RestartsThisWeek ?? 0,
                BrowsersLive: instance?.BrowsersLive ?? 0),
            Machine: new SystemOverviewMachine(
                Os: machine.Os ?? RuntimeInformation.OSDescription,
                UptimeSeconds: metrics.UptimeSeconds,
                RebootPending: machine.RebootPending,
                CpuTemperatureC: machine.CpuTemperatureC,
                CpuPercent: metrics.CpuPercent,
                Cores: metrics.Cores,
                MemoryUsedBytes: metrics.MemoryUsedBytes,
                MemoryTotalBytes: metrics.MemoryTotalBytes,
                DiskReadBytesPerSec: metrics.DiskReadBytesPerSec,
                DiskWriteBytesPerSec: metrics.DiskWriteBytesPerSec,
                DiskIops: metrics.DiskIops),
            Network: new SystemOverviewNetwork(
                Adapter: metrics.Adapter?.Name,
                LinkMbps: metrics.Adapter?.LinkMbps,
                Ip: metrics.Adapter?.Ip,
                InBytesPerSec: metrics.NetInBytesPerSec,
                OutBytesPerSec: metrics.NetOutBytesPerSec,
                Internet: instance?.Internet ?? false,
                DnsLookupMs: network?.DnsLookupMs,
                NasMs: network?.NasMs,
                NasName: network?.NasName,
                MetadataMs: network?.MetadataMs,
                ClockInSync: network?.ClockInSync),
            Drives: drives.Select(drive => BuildDrive(drive, metrics)).ToArray(),
            History: new SystemOverviewHistory(history.Cpu, history.Memory, history.Disk, history.NetIn));
    }

    private SystemOverviewDrive BuildDrive(DriveFacts drive, HostMetrics metrics)
    {
        metrics.Drives.TryGetValue(drive.Path, out var activity);
        var health = _driveHealth
            .GetOrAdd(drive.Path, path => new SlowValue<DriveHealthReading>(
                TimeSpan.FromMinutes(5),
                _ => Task.FromResult(_host.ReadDriveHealth(path)),
                _time,
                DriveHealthReading.Unknown))
            .Current;

        return new SystemOverviewDrive(
            Name: drive.Name,
            Path: drive.Path,
            TotalBytes: drive.TotalBytes,
            FreeBytes: drive.FreeBytes,
            DelunoBytes: drive.DelunoBytes,
            FullInDays: drive.FullInDays,
            ReadBytesPerSec: activity?.ReadBytesPerSec,
            WriteBytesPerSec: activity?.WriteBytesPerSec,
            Iops: activity?.Iops,
            BusyPercent: activity?.BusyPercent,
            Health: health.Health,
            HealthNote: health.Note);
    }

    private sealed record MachineFacts(string? Os, bool? RebootPending, double? CpuTemperatureC);
}
