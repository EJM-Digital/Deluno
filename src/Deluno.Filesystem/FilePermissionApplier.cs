using Deluno.Infrastructure.Processes;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Deluno.Platform.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// Applies the Import Policy "Set Permissions" setting (#766) to one file:
/// chmod, then chown its group, on non-Windows hosts only, and only when
/// enabled.
///
/// <para>One place, called by the import path and by <see cref="SubtitleFileWriter"/>
/// (#766, #838) alike, so a subtitle Deluno fetches gets the same permissions
/// as the media it belongs to rather than a second copy of this rule quietly
/// disagreeing with the first — which is exactly the gap #766's audit found:
/// the setting existed, the import path read it, and the subtitle writer never
/// asked. A static method taking its caller's own logger, rather than a
/// registered service, so callers already carrying an <c>ILogger</c> field
/// (both do) need no constructor change to use it.</para>
/// </summary>
public static class FilePermissions
{
    public static async Task ApplyAsync(
        string path, PlatformSettingsSnapshot settings, ILogger logger, CancellationToken cancellationToken)
    {
        if (!settings.SetPermissionsOnImport || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows is skipped outright: chmod/chown are Unix-only ideas,
            // and File.SetUnixFileMode throws PlatformNotSupportedException
            // there rather than doing nothing quietly.
            return;
        }

        if (!string.IsNullOrEmpty(settings.FolderChmod))
        {
            try
            {
                File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(settings.FolderChmod, 8));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
            {
                logger.LogWarning(exception, "Could not set permissions {Chmod} on {Path}. This only works when Deluno's own process owns the file.", settings.FolderChmod, path);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ChownGroup))
        {
            await TryChownGroupAsync(path, settings.ChownGroup, logger, cancellationToken);
        }
    }

    private static readonly TimeSpan ChgrpLimit = TimeSpan.FromSeconds(30);

    private static async Task TryChownGroupAsync(string path, string group, ILogger logger, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = "chgrp" };
        startInfo.ArgumentList.Add(group);
        startInfo.ArgumentList.Add(path);

        // Read as it arrives and stopped if it does not finish, so a stalled share cannot hold the import up (#1258).
        var result = await ChildProcess.RunAsync(startInfo, ChgrpLimit, cancellationToken);
        if (!result.Started)
        {
            logger.LogWarning("Could not run chgrp {Group} for {Path}: the chgrp binary was not found.", group, path);
            return;
        }

        if (result.TimedOut)
        {
            logger.LogWarning("chgrp {Group} for {Path} did not finish in time and was stopped.", group, path);
            return;
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning(
                "chgrp {Group} for {Path} exited with code {ExitCode}: {Error}. This only works when Deluno's own process owns the file.",
                group,
                path,
                result.ExitCode,
                result.Error.Trim());
        }
    }
}
