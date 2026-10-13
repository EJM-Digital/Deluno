using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Deluno.Infrastructure.Processes;
using Microsoft.Win32.SafeHandles;

namespace Deluno.Filesystem;

/// <summary>
/// A path's on-disk identity — which volume and which set of file data it
/// points at — read directly from the filesystem rather than guessed from
/// configuration (#819, #926). Two paths with the same identity are one
/// hardlink; different identities (or a path that no longer exists) are not,
/// whatever "same drive, hardlinks on" would have implied.
/// </summary>
public interface IFileIdentity
{
    /// <summary>Null when the path does not exist, or its identity could not be read.</summary>
    string? Identify(string path);
}

/// <summary>
/// Windows: <c>GetFileInformationByHandle</c>'s volume serial and file index -
/// the same pair Explorer's own "Hard Links" property page reads. Linux and
/// other Unix systems: <c>stat</c>'s device and inode number, read by running
/// the coreutils <c>stat</c> binary rather than a raw <c>libc</c> struct,
/// whose layout is not guaranteed stable across distributions and
/// architectures (see <see cref="ImportPipelineService"/>'s own hardlink
/// creation, which faces the same platform split).
/// </summary>
public sealed class FileIdentity : IFileIdentity
{
    public string? Identify(string path)
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsIdentity(path) : UnixIdentity(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    private static string? WindowsIdentity(string path)
    {
        using var handle = OpenReadHandle(path);
        if (handle is null || handle.IsInvalid)
        {
            return null;
        }

        return GetFileInformationByHandle(handle, out var info)
            ? $"{info.dwVolumeSerialNumber:x8}-{info.nFileIndexHigh:x8}{info.nFileIndexLow:x8}"
            : null;
    }

    private static SafeFileHandle? OpenReadHandle(string path)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>%d:%i</c> — device and inode — the same pair a Linux <c>stat</c>
    /// syscall answers with, read through the external binary that every base
    /// image and every dev machine this runs on already carries.
    /// </summary>
    private static readonly TimeSpan StatLimit = TimeSpan.FromSeconds(30);

    private static string? UnixIdentity(string path)
    {
        var startInfo = new ProcessStartInfo { FileName = "stat" };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("%d:%i");
        startInfo.ArgumentList.Add(path);

        // Read as it arrives and stopped if it does not finish: a stalled share cannot hold the caller up (#1258).
        var result = ChildProcess.Run(startInfo, StatLimit);

        // No `stat` binary on this system, or it did not answer. Deluno cannot tell, rather than guess.
        return result.Started && !result.TimedOut && result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

#pragma warning disable CS0649 // Populated entirely by the native call.
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME_LOCAL
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME_LOCAL ftCreationTime;
        public FILETIME_LOCAL ftLastAccessTime;
        public FILETIME_LOCAL ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
#pragma warning restore CS0649
}

/// <summary>
/// Whether a download's file and the library's copy of it are still one set
/// of file data (#819, #926) — the real question a hardlink count answers,
/// which "same drive, hardlinks on" only ever guessed at.
/// </summary>
public interface IHardlinkInspector
{
    /// <returns>
    /// True: still linked, sharing costs nothing. False: the library copy is
    /// gone or was replaced by a different file — a definite, actionable
    /// answer, not an error. Null: Deluno cannot tell (most often because the
    /// download's own file is missing too, a different problem).
    /// </returns>
    bool? StillLinked(string? downloadPath, string? libraryPath);
}

public sealed class HardlinkInspector(IFileIdentity identity) : IHardlinkInspector
{
    public HardlinkInspector() : this(new FileIdentity())
    {
    }

    public bool? StillLinked(string? downloadPath, string? libraryPath)
    {
        if (string.IsNullOrWhiteSpace(downloadPath) || string.IsNullOrWhiteSpace(libraryPath))
        {
            return null;
        }

        if (!File.Exists(libraryPath))
        {
            // Nothing left in the library to share with - gone or upgraded.
            // A definite, useful "no", not "cannot say".
            return false;
        }

        if (!File.Exists(downloadPath))
        {
            // The download's own copy is missing, which is a different
            // problem (#927) - this one has nothing to compare.
            return null;
        }

        var downloadIdentity = identity.Identify(downloadPath);
        var libraryIdentity = identity.Identify(libraryPath);
        return downloadIdentity is null || libraryIdentity is null
            ? null
            : string.Equals(downloadIdentity, libraryIdentity, StringComparison.Ordinal);
    }
}
