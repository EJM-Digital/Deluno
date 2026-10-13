using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Deluno.Integrations.DownloadClients;

/// <summary>What one scheduled or manual leftover-cleanup pass did (#932).</summary>
/// <param name="Removed">How many leftovers were old enough and clear of every hold condition, and so were deleted.</param>
/// <param name="Held">How many leftovers exist but were held back by at least one condition (too young, still shared, or unproven).</param>
public sealed record LeftoverDownloadCleanupResult(int Removed, int Held);

/// <summary>
/// Automatic clean-up of leftover download files (cleanup sheet row M1,
/// #932): the manual leftovers list on Queue only ever offers a person a
/// Keep/Remove click. This is the same rule, run on its own schedule and
/// widened with an age threshold, for leftovers nobody has looked at.
///
/// <para>Deliberately built on <see cref="IOrphanedDownloadService"/> rather
/// than a second scan: <c>ListAsync</c> already proves nothing claims an
/// item (no client, import, hand-off, or configured folder), and
/// <c>RemoveAsync</c> already refuses anything Deluno cannot prove it
/// downloaded and anything inside a library root. This adds the two
/// conditions that only make sense for an unattended pass: the item must
/// have sat long enough to be sure it is not mid-handoff to something that
/// has not recorded it yet, and it must not still be the other name of a
/// library file.</para>
/// </summary>
public interface ILeftoverDownloadCleanupService
{
    Task<LeftoverDownloadCleanupResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class LeftoverDownloadCleanupService(
    IOrphanedDownloadService orphanedDownloads,
    IPlatformSettingsRepository platformSettingsRepository,
    TimeProvider timeProvider,
    ILogger<LeftoverDownloadCleanupService> logger)
    : ILeftoverDownloadCleanupService
{
    public async Task<LeftoverDownloadCleanupResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        if (!settings.LeftoverDownloadCleanupEnabled)
        {
            return new LeftoverDownloadCleanupResult(0, 0);
        }

        var ageThreshold = TimeSpan.FromHours(Math.Max(1, settings.LeftoverDownloadCleanupAgeHours));
        var now = timeProvider.GetUtcNow();
        var candidates = await orphanedDownloads.ListAsync(cancellationToken);

        var removed = 0;
        var held = 0;
        foreach (var item in candidates)
        {
            if (now - item.LastWrittenUtc < ageThreshold)
            {
                // Too young by this pass's own threshold - the manual list
                // already applies the shorter, fixed grace window everything
                // shares, this is the separate, configurable one.
                held++;
                continue;
            }

            if (IsStillLinkedElsewhere(item.Path))
            {
                // More than one name still points at these bytes - a library
                // file, another client's copy, or a cross-seed Deluno left
                // alone on purpose. Deleting this name would free no disk at
                // all and might be the wrong name to lose.
                held++;
                continue;
            }

            var action = await orphanedDownloads.RemoveAsync(
                item.Path,
                cancellationToken,
                reason: $"it is over {DescribeAge(ageThreshold)} old and nothing is using it any more");
            if (action.Done)
            {
                removed++;
            }
            else
            {
                held++;
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Leftover-download cleanup removed {RemovedCount} item(s) older than {AgeHours}h that nothing was using.", removed, settings.LeftoverDownloadCleanupAgeHours);
        }

        return new LeftoverDownloadCleanupResult(removed, held);
    }

    private static string DescribeAge(TimeSpan threshold)
        => threshold.TotalHours % 24 == 0 && threshold.TotalHours >= 24
            ? $"{(int)(threshold.TotalHours / 24)} day{(threshold.TotalHours == 24 ? "" : "s")}"
            : $"{(int)threshold.TotalHours} hour{(threshold.TotalHours == 1 ? "" : "s")}";

    /// <summary>
    /// Whether another directory entry - a library file, another client's
    /// copy, or a cross-seed - still points at this file's own data, read
    /// from the filesystem's own link count rather than guessed at from
    /// configuration. A single-linked file (or a directory, which cannot
    /// itself be hardlinked) is never held back on this account.
    /// </summary>
    private bool IsStillLinkedElsewhere(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // A directory (a season pack's own folder, say) is never
                // itself the other name of a library file - only the files
                // inside one could be, and each is its own orphan entry.
                return false;
            }

            return LinkCountOf(path) > 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cannot tell, so held back rather than guessed at - the same
            // caution RemoveAsync already applies to a path it cannot reach.
            logger.LogWarning(exception, "Could not read the link count for {Path}.", path);
            return true;
        }
    }

    /// <summary>
    /// The filesystem's own hardlink count for a file. .NET has no portable
    /// API for this, so it is read the way any other tool on the host would:
    /// <c>GetFileInformationByHandle</c> on Windows, <c>stat</c> on Linux.
    /// </summary>
    private static int LinkCountOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsLinkCount.Read(path);
        }

        // Read as it arrives and stopped if it does not finish (#1258).
        var result = Deluno.Infrastructure.Processes.ChildProcess.Run(new ProcessStartInfo("stat", ["-c", "%h", path]), TimeSpan.FromSeconds(10));
        if (!result.Started)
        {
            throw new IOException("Could not start 'stat' to read the link count.");
        }

        if (result.ExitCode != 0 || !int.TryParse(result.Output.Trim(), out var count))
        {
            throw new IOException($"'stat' could not read the link count for '{path}'.");
        }

        return count;
    }
}

/// <summary>Windows-only P/Invoke for a file's hardlink count, isolated so the rest of the class stays platform-neutral to read.</summary>
internal static class WindowsLinkCount
{
    public static int Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException($"Could not read file information for '{path}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return (int)info.NumberOfLinks;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
