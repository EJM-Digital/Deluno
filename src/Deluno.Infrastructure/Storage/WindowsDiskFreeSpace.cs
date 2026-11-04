using System.Runtime.InteropServices;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// The one Windows call that asks a network share how full it is. Nothing but the call lives here (it cannot run
/// on any other system), so it is the one file of the volume reading left out of coverage.
/// </summary>
internal static class WindowsDiskFreeSpace
{
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    public static (ulong Free, ulong Total)? TryRead(string directory)
        => GetDiskFreeSpaceEx(directory, out var free, out var total, out _) ? (free, total) : null;
}
