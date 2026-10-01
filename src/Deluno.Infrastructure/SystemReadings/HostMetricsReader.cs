namespace Deluno.Infrastructure.SystemReadings;

/// <summary>One drive's disk activity over the window between two readings.</summary>
public sealed record DriveActivity(long ReadBytesPerSec, long WriteBytesPerSec, long Iops, double BusyPercent);

/// <summary>The fast readings of the machine: the ones that change second to second and are taken every two seconds.</summary>
/// <param name="HasBaseline">False for the very first reading: rates need two points, so they are zero until the next one.</param>
public sealed record HostMetrics(
    bool HasBaseline,
    long UptimeSeconds,
    double CpuPercent,
    int Cores,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskReadBytesPerSec,
    long DiskWriteBytesPerSec,
    long DiskIops,
    double DiskBusyPercent,
    NetworkAdapterReading? Adapter,
    long NetInBytesPerSec,
    long NetOutBytesPerSec,
    IReadOnlyDictionary<string, DriveActivity?> Drives)
{
    public double MemoryPercent => MemoryTotalBytes > 0 ? Math.Round(MemoryUsedBytes * 100d / MemoryTotalBytes, 1) : 0;
}

/// <summary>
/// Turns two successive raw readings into rates: processor load, disk throughput, operations a second,
/// busy share and network speed. It holds the previous reading, so one instance serves the whole
/// process and a second caller would corrupt the baseline; the sampler is the only one that asks.
/// </summary>
public sealed class HostMetricsReader(IHostReadingSource source, TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private DateTimeOffset _previousAt;
    private CpuTimes? _previousCpu;
    private DiskSnapshot? _previousDisks;
    private NetworkAdapterReading? _previousAdapter;

    /// <summary>When the last reading was taken; <see cref="DateTimeOffset.MinValue"/> before the first.</summary>
    public DateTimeOffset LastReadAt
    {
        get
        {
            lock (_gate)
            {
                return _previousAt == default ? DateTimeOffset.MinValue : _previousAt;
            }
        }
    }

    /// <param name="drivePaths">A folder on each drive whose own activity is wanted.</param>
    public HostMetrics Read(IReadOnlyList<string> drivePaths)
    {
        var now = timeProvider.GetUtcNow();
        var cpu = source.ReadCpuTimes();
        var memory = source.ReadMemory();
        var disks = source.ReadDisks();
        var adapter = source.ReadDefaultRouteAdapter();
        var uptime = source.ReadUptimeSeconds() ?? 0;

        lock (_gate)
        {
            var seconds = _previousAt == default ? 0 : (now - _previousAt).TotalSeconds;
            var hasBaseline = seconds > 0;

            var cpuPercent = hasBaseline && cpu is { } c && _previousCpu is { } pc ? CpuPercent(pc, c) : 0;

            long diskRead = 0, diskWrite = 0, diskIops = 0;
            var diskBusy = 0d;
            if (hasBaseline && disks.Machine is { } machine && _previousDisks?.Machine is { } previousMachine)
            {
                diskRead = Rate(previousMachine.BytesRead, machine.BytesRead, seconds) ?? 0;
                diskWrite = Rate(previousMachine.BytesWritten, machine.BytesWritten, seconds) ?? 0;
                diskIops = Rate(previousMachine.Operations, machine.Operations, seconds) ?? 0;
                diskBusy = BusyPercent(previousMachine, machine) ?? 0;
            }

            var drives = new Dictionary<string, DriveActivity?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in drivePaths)
            {
                drives[path] = hasBaseline ? ActivityOf(path, disks, _previousDisks, seconds) : null;
            }

            long netIn = 0, netOut = 0;
            if (hasBaseline && adapter is not null && _previousAdapter is not null
                && string.Equals(adapter.Name, _previousAdapter.Name, StringComparison.Ordinal))
            {
                netIn = Rate(_previousAdapter.BytesIn, adapter.BytesIn, seconds) ?? 0;
                netOut = Rate(_previousAdapter.BytesOut, adapter.BytesOut, seconds) ?? 0;
            }

            _previousAt = now;
            _previousCpu = cpu ?? _previousCpu;
            _previousDisks = disks;
            _previousAdapter = adapter;

            var total = memory?.Total ?? 0;
            return new HostMetrics(
                hasBaseline,
                uptime,
                cpuPercent,
                Environment.ProcessorCount,
                memory is { } m ? Math.Max(0, m.Total - m.Available) : 0,
                total,
                diskRead,
                diskWrite,
                diskIops,
                diskBusy,
                adapter,
                netIn,
                netOut,
                drives);
        }
    }

    private DriveActivity? ActivityOf(string path, DiskSnapshot current, DiskSnapshot? previous, double seconds)
    {
        var key = source.VolumeKeyOf(path);
        if (key is null
            || !current.Volumes.TryGetValue(key, out var now)
            || previous is null
            || !previous.Volumes.TryGetValue(key, out var before))
        {
            return null;
        }

        return new DriveActivity(
            Rate(before.BytesRead, now.BytesRead, seconds) ?? 0,
            Rate(before.BytesWritten, now.BytesWritten, seconds) ?? 0,
            Rate(before.Operations, now.Operations, seconds) ?? 0,
            BusyPercent(before, now) ?? 0);
    }

    /// <summary>The share of the window the processors were not idle, 0 to 100.</summary>
    public static double CpuPercent(CpuTimes before, CpuTimes now)
    {
        if (now.Total <= before.Total || now.Idle < before.Idle)
        {
            return 0;
        }

        var total = (double)(now.Total - before.Total);
        var idle = (double)(now.Idle - before.Idle);
        return Math.Round(Math.Clamp((1d - idle / total) * 100d, 0, 100), 1);
    }

    /// <summary>A rate from two cumulative counters; null when the counter went backwards (it wrapped or restarted).</summary>
    public static long? Rate(long before, long now, double seconds)
        => seconds > 0 && now >= before ? (long)Math.Round((now - before) / seconds) : null;

    /// <summary>The share of the window a disk was busy: its idle time against the time that passed.</summary>
    public static double? BusyPercent(DiskCounters before, DiskCounters now)
    {
        var query = now.QueryTime - before.QueryTime;
        var idle = now.IdleTime - before.IdleTime;
        return query > 0 && idle >= 0
            ? Math.Round(Math.Clamp((1d - (double)idle / query) * 100d, 0, 100), 1)
            : null;
    }
}
