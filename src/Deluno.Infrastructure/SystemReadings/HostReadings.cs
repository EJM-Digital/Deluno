namespace Deluno.Infrastructure.SystemReadings;

/// <summary>Cumulative processor time since boot, in any consistent unit.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

/// <summary>
/// One volume's cumulative disk counters. <see cref="IdleTime"/> and <see cref="QueryTime"/> are in the
/// same unit, so busy is the share of the window that was not idle.
/// </summary>
public sealed record DiskCounters(long BytesRead, long BytesWritten, long Operations, long IdleTime, long QueryTime);

/// <summary>Cumulative bytes on the adapter that carries the default route, plus what is known about it.</summary>
public sealed record NetworkAdapterReading(string Name, string? Ip, long? LinkMbps, long BytesIn, long BytesOut);

/// <summary>A drive's own opinion of itself: good, warning, failing or unknown, with a plain note when it is not good.</summary>
public sealed record DriveHealthReading(string Health, string? Note)
{
    public static readonly DriveHealthReading Unknown = new("unknown", null);
}

public sealed record OperatingSystemReading(string Caption, string? Version)
{
    public string Describe() => string.IsNullOrWhiteSpace(Version) ? Caption : $"{Caption} ({Version})";
}

/// <summary>
/// The operating system's raw readings. Everything is cumulative or instantaneous and nothing here
/// computes a rate: <see cref="HostMetricsReader"/> does that between two readings. A reading the
/// system cannot give is null, never a guess. There is one implementation for Windows (kernel calls,
/// registry and WMI) and one for Linux and Docker (/proc and /sys), and tests use fakes.
/// </summary>
public interface IHostReadingSource
{
    CpuTimes? ReadCpuTimes();

    /// <summary>Total and available physical memory in bytes.</summary>
    (long Total, long Available)? ReadMemory();

    long? ReadUptimeSeconds();

    OperatingSystemReading? ReadOperatingSystem();

    bool? ReadRebootPending();

    double? ReadCpuTemperatureC();

    /// <summary>Disk counters: one per local volume, keyed by the volume's own key (<see cref="VolumeKeyOf"/>), and the whole machine's together.</summary>
    DiskSnapshot ReadDisks();

    /// <summary>The key that names the local volume holding <paramref name="path"/> in <see cref="DiskSnapshot.Volumes"/>; null for a network share or an unknown place.</summary>
    string? VolumeKeyOf(string path);

    NetworkAdapterReading? ReadDefaultRouteAdapter();

    /// <summary>The drive's health. Slow on Windows (WMI): callers cache it.</summary>
    DriveHealthReading ReadDriveHealth(string path);
}

/// <summary>Every local volume's counters and the machine's disks added together (each physical disk once).</summary>
public sealed record DiskSnapshot(IReadOnlyDictionary<string, DiskCounters> Volumes, DiskCounters? Machine)
{
    public static readonly DiskSnapshot Empty = new(new Dictionary<string, DiskCounters>(), null);
}

public static class DiskCounterMath
{
    public static DiskCounters Sum(IEnumerable<DiskCounters> counters)
    {
        long read = 0, written = 0, operations = 0, idle = 0, query = 0;
        foreach (var counter in counters)
        {
            read += counter.BytesRead;
            written += counter.BytesWritten;
            operations += counter.Operations;
            idle += counter.IdleTime;
            query += counter.QueryTime;
        }

        return new DiskCounters(read, written, operations, idle, query);
    }
}
