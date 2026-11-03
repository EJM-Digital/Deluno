namespace Deluno.Tray;

/// <summary>
/// How the Windows service launches a bundled client so it reads the settings the install set up (#1452). Qt (qBittorrent) asks Windows for
/// the Roaming folder and ignores the APPDATA the service sets, so under LocalSystem qBittorrent opened a fresh profile in the system
/// profile (Web UI off, no login) and never answered. SABnzbd is not changed here: in session 0 it stops in its own Windows-service check
/// whatever the arguments (see the tests), so it needs a different way to start.
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
            // person's settings (Web UI on, port, login, folders) are copied there at each start when they are newer than the profile's.
            var profile = dataRoot.TrimEnd('\\', '/') + @"\service-profiles\qbittorrent";
            return new Launch(
                Join(arguments, $"--profile={profile}"),
                folders is { } f ? f.AppData + @"\qBittorrent\qBittorrent.ini" : null,
                profile + @"\qBittorrent\config\qBittorrent.ini");
        }

        return new Launch(arguments ?? string.Empty, null, null);
    }

    /// <summary>True when the person's settings file exists and the service profile has none or an older one.</summary>
    public static bool ShouldSeed(DateTime? personWrittenUtc, DateTime? profileWrittenUtc)
        => personWrittenUtc is { } person && (profileWrittenUtc is null || person > profileWrittenUtc);

    private static string Join(string? arguments, string extra)
        => string.IsNullOrWhiteSpace(arguments) ? extra : arguments + " " + extra;
}
