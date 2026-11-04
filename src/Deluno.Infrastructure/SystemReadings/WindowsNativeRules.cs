namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The arithmetic and naming around Windows' own calls (WindowsNative), kept apart from the calls so it can be
/// tested on any system.
/// </summary>
public static class WindowsNativeRules
{
    /// <summary>A Windows file time arrives as two halves.</summary>
    public static ulong Combine(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Busy time is kernel plus user time; the kernel figure already includes the idle time.</summary>
    public static CpuTimes CpuTimesOf(ulong idle, ulong kernel, ulong user) => new(idle, kernel + user);

    /// <summary>A volume root is only worth asking about when it names a drive.</summary>
    public static bool IsDriveRoot(string? volumeRoot) => !string.IsNullOrWhiteSpace(volumeRoot) && volumeRoot.Contains(':');

    /// <summary>The raw device name of a drive root such as C:\ , which is what the counters are asked of.</summary>
    public static string DeviceNameFor(string volumeRoot)
        => new string((char)92, 2) + "." + (char)92 + volumeRoot.TrimEnd((char)92, (char)47);

    /// <summary>Whether CreateFile gave back a usable handle: neither nothing nor the invalid handle.</summary>
    public static bool IsOpened(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    public static DiskCounters CountersOf(long bytesRead, long bytesWritten, uint readCount, uint writeCount, long idleTime, long queryTime)
        => new(bytesRead, bytesWritten, (long)readCount + writeCount, idleTime, queryTime);
}
