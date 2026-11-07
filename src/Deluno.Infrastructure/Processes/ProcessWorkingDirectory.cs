namespace Deluno.Infrastructure.Processes;

/// <summary>
/// The folder a program Deluno starts runs in when it has no folder of its own (#1532). A program inherits the folder
/// of whoever started it, and Deluno's tray and host run from the install folder, so a client started without a working
/// directory (NZBGet was) held that folder open: Windows would not rename it, Setup failed "Access is denied" and the
/// update never applied. Nothing Deluno starts may be left standing in the install folder.
/// </summary>
public static class ProcessWorkingDirectory
{
    /// <summary>A folder that always exists and is never Deluno's install folder: Windows' system folder, or the temp folder elsewhere.</summary>
    public static string Neutral
    {
        get
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(Environment.SystemDirectory))
            {
                return Environment.SystemDirectory;
            }

            return Path.GetTempPath();
        }
    }

    /// <summary>
    /// The folder a program is run in: its own folder (where a client keeps its files) unless that is missing or is
    /// <paramref name="installFolder"/> or inside it, then <see cref="Neutral"/>.
    /// </summary>
    public static string For(string? programPath, string? installFolder = null)
    {
        var folder = string.IsNullOrWhiteSpace(programPath) ? null : Path.GetDirectoryName(programPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || IsInside(folder, installFolder ?? AppContext.BaseDirectory))
        {
            return Neutral;
        }

        return folder;
    }

    /// <summary>True when <paramref name="folder"/> is <paramref name="root"/> or below it.</summary>
    public static bool IsInside(string folder, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(top, comparison)
            || full.StartsWith(top + Path.DirectorySeparatorChar, comparison);
    }
}
