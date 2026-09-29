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
}
