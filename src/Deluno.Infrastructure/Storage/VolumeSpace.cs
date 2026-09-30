using System.Runtime.InteropServices;

namespace Deluno.Infrastructure.Storage;

/// <summary>Free and total bytes of the drive a folder lives on.</summary>
public sealed record VolumeSpaceReading(string VolumeKey, long FreeBytes, long TotalBytes);

/// <summary>
/// The one place Deluno asks a drive how full it is (#1021), so the disk-space
/// address and Monitoring's storage summary read it the same way.
/// </summary>
public static class VolumeSpace
{
    /// <summary>
    /// True for a Windows network path such as a server, a share and a folder
    /// joined by backslashes. .NET's DriveInfo only accepts a drive letter and
    /// throws for these, so they are sized through the share itself. A Linux or
    /// macOS mount is an ordinary path.
    /// </summary>
    public static bool IsNetworkSharePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Length > 2
           && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
           && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
           && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// <summary>The server and share part of a network path, the same for every folder on that share; null when it has none.</summary>
    public static string? NetworkShareRoot(string? path)
    {
        if (!IsNetworkSharePath(path))
        {
            return null;
        }

        var parts = path!.Replace('/', '\\').TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : @"\\" + parts[0] + @"\" + parts[1];
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    /// <summary>
    /// The reading for the drive holding <paramref name="path"/>, or null when it
    /// cannot be read (not a real drive, a network share the OS will not size,
    /// nothing mounted). A folder that does not exist yet is read through its
    /// nearest folder that does, because a library root that is not created
    /// still lives on a drive.
    /// </summary>
    public static VolumeSpaceReading? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var probe = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && NetworkShareRoot(probe) is { } share)
            {
                return TryReadShare(share, probe);
            }

            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent))
                {
                    return null;
                }

                probe = parent;
            }

            var drive = new DriveInfo(probe);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return null;
            }

            // On Windows a drive letter names a volume. Elsewhere any path is a
            // drive, so two folders share one only when the drive says the same
            // thing about itself.
            var key = OperatingSystem.IsWindows()
                ? (Path.GetPathRoot(probe) ?? probe).ToUpperInvariant()
                : $"{drive.DriveFormat}:{drive.TotalSize}";
            return new VolumeSpaceReading(key, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static VolumeSpaceReading? TryReadShare(string share, string path)
    {
        // A folder that is not there yet is read through the nearest one that is, down to the share itself.
        var probe = path;
        while (!Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent.Length < share.Length)
            {
                return null;
            }

            probe = parent;
        }

        if (!GetDiskFreeSpaceEx(probe, out var free, out var total, out _) || total == 0)
        {
            return null;
        }

        return new VolumeSpaceReading(share.ToUpperInvariant(), (long)Math.Min(free, long.MaxValue), (long)Math.Min(total, long.MaxValue));
    }
}
