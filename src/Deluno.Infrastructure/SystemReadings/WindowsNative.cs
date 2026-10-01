using System.Runtime.InteropServices;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The few documented kernel32 calls Deluno reads the machine with: processor time, physical memory
/// and a volume's disk counters. No performance counters and no WMI for these, because counter names
/// are localised and the service behind them can be switched off. Shared by the System overview and
/// <see cref="Observability.MachineProbe"/>, so the two cannot disagree about how a reading is taken.
/// </summary>
internal static class WindowsNative
{
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;

        // WCHAR[8], marshalled as raw units rather than ByValTStr. ByValTStr
        // without an explicit CharSet marshals as ANSI, which makes the struct
        // eight bytes short, and DeviceIoControl answers a too-small output
        // buffer with ERROR_INVALID_PARAMETER, which reads exactly like an
        // unsupported call. Do not "tidy" it back.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public ushort[] StorageManagerName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Processor time of the whole machine since boot, in 100 ns ticks. Kernel time includes idle, so total is kernel plus user.</summary>
    public static CpuTimes? TryReadCpuTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return GetSystemTimes(out var idle, out var kernel, out var user)
                ? new CpuTimes(idle.Value, kernel.Value + user.Value)
                : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static (long Total, long Available)? TryReadMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys) : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whole-volume load, straight from the volume. Opening it needs no elevation and no counter
    /// service, but a locked, missing or network volume will refuse, hence null rather than a throw.
    /// <paramref name="volumeRoot"/> is a drive root such as <c>C:\</c>.
    /// </summary>
    public static DiskCounters? TryReadVolumeCounters(string volumeRoot)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumeRoot) || !volumeRoot.Contains(':'))
        {
            return null;
        }

        var handle = IntPtr.Zero;
        try
        {
            var device = @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Opened with no access rights at all: GENERIC_READ on a raw volume needs administrator,
            // while an IOCTL that only reads counters does not need to read the volume. Falling back to
            // GENERIC_READ covers the case where Deluno is elevated and something rejects zero access.
            foreach (var access in new[] { 0u, GenericRead })
            {
                var opened = CreateFileW(device, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (opened != IntPtr.Zero && opened != new IntPtr(-1))
                {
                    handle = opened;
                    break;
                }
            }

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var size = Marshal.SizeOf<DiskPerformance>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
                {
                    return null;
                }

                var performance = Marshal.PtrToStructure<DiskPerformance>(buffer);
                return new DiskCounters(
                    performance.BytesRead,
                    performance.BytesWritten,
                    (long)performance.ReadCount + performance.WriteCount,
                    performance.IdleTime,
                    performance.QueryTime);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }
}
