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

    /// <summary>What a drive says about itself.</summary>
    public sealed record DriveFacts(long TotalSize, long AvailableFreeSpace, string DriveFormat);

    /// <summary>Every question TryRead asks the system, so each can be answered by a test on any system.</summary>
    public sealed record VolumeProbes(
        bool IsWindows,
        Func<string, string> FullPath,
        Func<string, bool> DirectoryExists,
        Func<string, DriveFacts> DriveOf,
        Func<string, (ulong Free, ulong Total)?> ShareSpace)
    {
        public static VolumeProbes ThisSystem { get; } = new(
            OperatingSystem.IsWindows(),
            Path.GetFullPath,
            Directory.Exists,
            probe =>
            {
                var drive = new DriveInfo(probe);
                // A drive that is not ready refuses to say its size, which the catch below turns into no reading.
                return new DriveFacts(drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat);
            },
            WindowsDiskFreeSpace.TryRead);
    }

    public static VolumeSpaceReading? TryRead(string? path) => TryRead(path, VolumeProbes.ThisSystem);

    public static VolumeSpaceReading? TryRead(string? path, VolumeProbes system)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var probe = system.FullPath(path);
            if (system.IsWindows && NetworkShareRoot(probe) is { } share)
            {
                return TryReadShare(share, probe, system);
            }

            while (!system.DirectoryExists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent))
                {
                    return null;
                }

                probe = parent;
            }

            var drive = system.DriveOf(probe);
            if (drive.TotalSize <= 0)
            {
                return null;
            }

            // On Windows a drive letter names a volume. Elsewhere any path is a
            // drive, so two folders share one only when the drive says the same
            // thing about itself.
            var key = system.IsWindows
                ? Path.GetPathRoot(probe)!.ToUpperInvariant()
                : $"{drive.DriveFormat}:{drive.TotalSize}";
            return new VolumeSpaceReading(key, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A share path's parent, read from the text with either slash: Path.GetDirectoryName follows the host's own rules, and on
    /// Linux it folds the leading "//" of a share, so the walk below fell short of the share there (CI, 5 Nov).
    /// </summary>
    public static string? ShareParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut <= 1 ? null : trimmed[..cut];
    }

    private static VolumeSpaceReading? TryReadShare(string share, string path, VolumeProbes system)
    {
        // A folder that is not there yet is read through the nearest one that is, down to the share itself.
        var probe = path;
        while (!system.DirectoryExists(probe))
        {
            var parent = ShareParentOf(probe);
            if (parent is null || parent.Length < share.Length)
            {
                return null;
            }

            probe = parent;
        }

        if (system.ShareSpace(probe) is not { } space || space.Total == 0)
        {
            return null;
        }

        return new VolumeSpaceReading(share.ToUpperInvariant(), (long)Math.Min(space.Free, long.MaxValue), (long)Math.Min(space.Total, long.MaxValue));
    }
}
