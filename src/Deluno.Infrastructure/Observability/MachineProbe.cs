using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var io = ReadProcessIo();
        var disk = ReadDiskCounters(volumePath);

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
                TotalMemoryBytes: ReadTotalMemoryBytes(),
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

    private static ProcessIoCounters? ReadProcessIo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var counters)
                ? new ProcessIoCounters(counters.ReadTransferCount, counters.WriteTransferCount)
                : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long? ReadTotalMemoryBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and var available ? available : null;
        }

        return WindowsNative.TryReadMemory()?.Total;
    }

    /// <summary>
    /// Whole-volume load, straight from the volume. Opening it needs no
    /// elevation and no counter service, but a locked, missing or network
    /// volume will simply refuse, hence null rather than a throw.
    /// </summary>
    private static DiskCounters? ReadDiskCounters(string? volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(volumePath));
            if (string.IsNullOrWhiteSpace(root) || !root.Contains(':'))
            {
                return null;
            }

            return WindowsNative.TryReadVolumeCounters(root) is { } counters
                ? new DiskCounters(counters.BytesRead, counters.BytesWritten, counters.IdleTime, counters.QueryTime)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record ProcessIoCounters(ulong ReadBytes, ulong WriteBytes);

    private sealed record DiskCounters(long BytesRead, long BytesWritten, long IdleTime, long QueryTime);

    // ── Windows interop ───────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }


    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

}
