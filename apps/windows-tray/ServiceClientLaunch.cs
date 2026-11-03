namespace Deluno.Tray;

/// <summary>
/// How the Windows service launches a bundled client so it reads the settings the install set up (#1452). Qt (qBittorrent) asks Windows for
/// the Roaming folder and ignores the APPDATA the service sets, so under LocalSystem qBittorrent opened a fresh profile in the system
/// profile (Web UI off, no login) and never answered; SABnzbd is told its settings file outright.
/// </summary>
public static class ServiceClientLaunch
{
    public sealed record Launch(string Arguments, string? SeedFrom, string? SeedTo);

    public static Launch Plan(string path, string? arguments, string dataRoot)
    {
        var exe = Path.GetFileName(path.Replace('/', '\\'));
        var folders = ServiceProcessStarter.ProfileFoldersFor(dataRoot);
        if (string.Equals(exe, "qbittorrent.exe", StringComparison.OrdinalIgnoreCase))
        {
            // qBittorrent keeps <profile>\qBittorrent\config\qBittorrent.ini under --profile, not where it keeps it by default, so the
            // person's settings (Web UI on, port, login, folders) are copied there once, when it has no file yet.
            var profile = dataRoot.TrimEnd('\\', '/') + @"\service-profiles\qbittorrent";
            return new Launch(
                Join(arguments, $"--profile={profile}"),
                folders is { } f ? f.AppData + @"\qBittorrent\qBittorrent.ini" : null,
                profile + @"\qBittorrent\config\qBittorrent.ini");
        }

        if (string.Equals(exe, "SABnzbd.exe", StringComparison.OrdinalIgnoreCase) && folders is { } sab)
        {
            return new Launch(Join(arguments, $"-f \"{sab.LocalAppData}\\sabnzbd\\sabnzbd.ini\""), null, null);
        }

        return new Launch(arguments ?? string.Empty, null, null);
    }

    private static string Join(string? arguments, string extra)
        => string.IsNullOrWhiteSpace(arguments) ? extra : arguments + " " + extra;
}
