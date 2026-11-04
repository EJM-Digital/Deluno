using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// Which mounted volume a folder is on: one rule for the library health row, the import preview's hard-link promise and the
/// sharing footprint (#1464). On Linux and in the Docker image every path is rooted at "/", so the path's own root said one
/// volume for a downloads mount and a media mount that a hard link cannot cross.
/// </summary>
public static class MountedVolumes
{
    private const string LinuxMountTable = "/proc/self/mounts";

    /// <summary>
    /// This machine's mounted roots. On Linux they are read from the mount table as text, never through
    /// <see cref="DriveInfo.GetDrives"/>: that looks at every mount, and on a CI runner a mount that did not answer kept a
    /// finished test run from exiting for eight minutes. On Windows they are the drive letters, which costs no disk access.
    /// </summary>
    public static IReadOnlyList<string> Roots()
        => Roots(
            OperatingSystem.IsLinux(),
            ReadLinuxMountTable,
            DriveNames);

    /// <summary>The lines of the Linux mount table.</summary>
    public static IEnumerable<string> ReadLinuxMountTable() => File.ReadLines(LinuxMountTable);

    /// <summary>The names of the drives, for systems that have them.</summary>
    public static IEnumerable<string> DriveNames() => DriveInfo.GetDrives().Select(drive => drive.Name);

    /// <summary>The same, with the system's answers handed in so each system's way can be shown on any other.</summary>
    public static IReadOnlyList<string> Roots(bool isLinux, Func<IEnumerable<string>> mountTable, Func<IEnumerable<string>> drives)
    {
        try
        {
            return isLinux ? ParseMountTable(mountTable()) : [.. drives()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The mount point of each line of a Linux mount table: the second field, with its octal escapes undone (\040 is a space).</summary>
    public static IReadOnlyList<string> ParseMountTable(IEnumerable<string> lines)
        => [.. lines
            .Select(line => line.Split(' '))
            .Where(fields => fields.Length > 1)
            .Select(fields => Regex.Replace(fields[1], @"\\([0-7]{3})", escape => ((char)Convert.ToInt32(escape.Groups[1].Value, 8)).ToString()))];

    /// <summary>
    /// The longest root that holds a full path, on a folder boundary so /data does not hold /database; null when none does.
    /// Windows compares without case, as its file system does.
    /// </summary>
    public static string? RootOf(string fullPath, IReadOnlyList<string> roots)
        => RootOf(fullPath, roots, PathComparison);

    /// <summary>How this system compares folder names: without case on Windows, with case elsewhere.</summary>
    public static StringComparison PathComparison => PathComparisonFor(OperatingSystem.IsWindows());

    /// <summary>The same, once it is known whether this is Windows.</summary>
    public static StringComparison PathComparisonFor(bool isWindows)
        => isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string? RootOf(string fullPath, IReadOnlyList<string> roots, StringComparison comparison)
        => roots
            .Where(root => root.Length > 0 && fullPath.StartsWith(root, comparison)
                && (fullPath.Length == root.Length || root[^1] is '/' or '\\' || fullPath[root.Length] is '/' or '\\'))
            .MaxBy(root => root.Length);
}
