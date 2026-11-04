using System.Diagnostics;
using Deluno.Contracts;
using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Infrastructure.Observability;

/// <summary>Takes one reading of how hard the machine is working (#272).</summary>
public interface IMachineProbe
{
    /// <summary>
    /// A reading covering the period since the previous call. Rates need two
    /// points, so the very first call after startup reports zero rates rather
    /// than inventing a baseline.
    /// </summary>
    MachineTelemetrySample Read(string? volumePath);
}

/// <summary>
/// Reads CPU, memory and disk load straight from the operating system (#272).
///
/// Deliberately no performance counters and no WMI. Counter category names are
/// localised, the service they depend on can be disabled, and both drag in a
/// package for four numbers. Everything here is either a plain .NET API or a
/// single documented kernel32 call.
///
/// Two disk readings, and having both is the point: Deluno's own I/O answers
/// "is this Deluno hammering the disk", and the whole-volume figure answers "is
/// the drive saturated by something else". One without the other cannot tell
/// those apart, which is exactly the question someone opens a dashboard to ask.
///
/// Anything it cannot read comes back null. A missing series is a gap in a
/// chart; it is never a reason to take the sampler down.
/// </summary>
public sealed class MachineProbe(TimeProvider timeProvider) : IMachineProbe
{
    private readonly Lock _sync = new();
    private DateTimeOffset _lastReadUtc;
    private TimeSpan _lastProcessorTime;
    private ulong _lastProcessReadBytes;
    private ulong _lastProcessWriteBytes;
    private DiskCounters? _lastDisk;

    public MachineTelemetrySample Read(string? volumePath)
    {
        var now = timeProvider.GetUtcNow();

        using var process = Process.GetCurrentProcess();
        var processorTime = process.TotalProcessorTime;
        var workingSet = process.WorkingSet64;
        return Sample(now, processorTime, workingSet, WindowsNative.TryReadProcessIo(), ReadDiskCounters(volumePath), TotalMemoryOf(WindowsNative.TryReadMemory(), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
    }

    /// <summary>
    /// One sample from readings already taken, set against the last one. The system's readings are handed in so the
    /// arithmetic can be shown on any system.
    /// </summary>
    public MachineTelemetrySample Sample(
        DateTimeOffset now,
        TimeSpan processorTime,
        long workingSet,
        ProcessIoCounters? io,
        DiskCounters? disk,
        long? totalMemoryBytes)
    {
        lock (_sync)
        {
            var elapsed = _lastReadUtc == default ? TimeSpan.Zero : now - _lastReadUtc;
            var seconds = elapsed.TotalSeconds;

            // The first call has nothing to measure against. Reporting zero is
            // honest; extrapolating from process start would attribute a whole
            // uptime's worth of I/O to one minute.
            var cpuPercent = seconds <= 0
                ? 0
                : Math.Clamp(
                    (processorTime - _lastProcessorTime).TotalSeconds / (seconds * Math.Max(1, Environment.ProcessorCount)) * 100,
                    0,
                    100);

            var readRate = RatePerSecond(io?.ReadBytes, _lastProcessReadBytes, seconds);
            var writeRate = RatePerSecond(io?.WriteBytes, _lastProcessWriteBytes, seconds);

            double? diskBusy = null;
            long? diskRead = null;
            long? diskWrite = null;
            if (disk is { } current && _lastDisk is { } previous && seconds > 0)
            {
                // IdleTime and QueryTime are both 100ns ticks. Busy is whatever
                // share of the window the volume was not idle for.
                var queryDelta = current.QueryTime - previous.QueryTime;
                var idleDelta = current.IdleTime - previous.IdleTime;
                if (queryDelta > 0)
                {
                    diskBusy = Math.Round(Math.Clamp((1d - (double)idleDelta / queryDelta) * 100, 0, 100), 1);
                }

                diskRead = RatePerSecond(current.BytesRead, previous.BytesRead, seconds);
                diskWrite = RatePerSecond(current.BytesWritten, previous.BytesWritten, seconds);
            }

            _lastReadUtc = now;
            _lastProcessorTime = processorTime;
            if (io is { } counters)
            {
                _lastProcessReadBytes = counters.ReadBytes;
                _lastProcessWriteBytes = counters.WriteBytes;
            }

            if (disk is not null)
            {
                _lastDisk = disk;
            }

            return new MachineTelemetrySample(
                CapturedUtc: now,
                CpuPercent: Math.Round(cpuPercent, 1),
                MemoryBytes: workingSet,
                TotalMemoryBytes: totalMemoryBytes,
                ProcessReadBytesPerSecond: readRate ?? 0,
                ProcessWriteBytesPerSecond: writeRate ?? 0,
                DiskBusyPercent: diskBusy,
                DiskReadBytesPerSecond: diskRead,
                DiskWriteBytesPerSecond: diskWrite);
        }
    }

    /// <summary>
    /// Counters are cumulative and unsigned. A negative delta means the counter
    /// wrapped or the source restarted, and reporting a vast negative rate would
    /// be worse than reporting none.
    /// </summary>
    private static long? RatePerSecond(ulong? current, ulong previous, double seconds)
    {
        if (current is not { } value || seconds <= 0 || value < previous)
        {
            return null;
        }

        return (long)Math.Round((value - previous) / seconds);
    }

    private static long? RatePerSecond(long? current, long previous, double seconds)
        => current is { } value && seconds > 0 && value >= previous
            ? (long)Math.Round((value - previous) / seconds)
            : null;

    /// <summary>The machine's memory: what Windows says when it says, otherwise what the runtime can see.</summary>
    public static long? TotalMemoryOf((long Total, long Available)? windows, long runtimeAvailableBytes)
        => windows is { } known ? known.Total : runtimeAvailableBytes > 0 ? runtimeAvailableBytes : null;

    private static DiskCounters? ReadDiskCounters(string? volumePath)
    {
        if (string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        try
        {
            // A full path always has a root; the reader itself turns away one that is not a Windows drive.
            return WindowsNative.TryReadVolumeCounters(Path.GetPathRoot(Path.GetFullPath(volumePath))!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
