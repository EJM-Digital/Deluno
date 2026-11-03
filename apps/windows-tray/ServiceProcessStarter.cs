using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>
/// Starts a bundled client from the Windows service (#1452). The service has no desktop, so every client starts with no window.
/// It usually runs as LocalSystem, whose profile is not the person's: qBittorrent, Deluge and SABnzbd keep their settings
/// under the profile's AppData, where the installer seeded them, so they are started with the profile folders the data folder
/// lives in. Every client Deluno bundles is started as a process; none is registered as a Windows service of its own
/// (Transmission's own service is switched off at install).
/// </summary>
public sealed class ServiceProcessStarter(string dataRoot) : IProcessStarter
{
    public IStartedProcess? Start(string path, string? arguments) => Start(path, arguments, hidden: true);

    public IStartedProcess? Start(string path, string? arguments, bool hidden)
    {
        try
        {
            var info = new ProcessStartInfo(path, arguments ?? string.Empty)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (ProfileFoldersFor(dataRoot) is { } folders)
            {
                info.Environment["APPDATA"] = folders.AppData;
                info.Environment["LOCALAPPDATA"] = folders.LocalAppData;
            }

            var process = Process.Start(info);
            return process is null ? null : new Started(process);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The Roaming and Local folders of the profile the data folder sits in (a path with an <c>AppData\Local</c> part), or null
    /// when the data folder is somewhere else, which leaves the client's environment as the service has it.
    /// </summary>
    public static (string AppData, string LocalAppData)? ProfileFoldersFor(string dataRoot)
    {
        var parts = dataRoot.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i + 1 < parts.Length; i++)
        {
            if (string.Equals(parts[i], "AppData", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[i + 1], "Local", StringComparison.OrdinalIgnoreCase))
            {
                var profile = string.Join('\\', parts.Take(i + 1));
                return (profile + "\\Roaming", profile + "\\Local");
            }
        }

        return null;
    }

    private sealed class Started(Process process) : IStartedProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.HasExited ? process.ExitCode : -1;

        public void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to end.
            }
        }
    }
}
