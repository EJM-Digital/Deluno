using System.Globalization;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1452, from the VM after a restart with the service running (rc.30.37 candidate, 3 Nov): qBittorrent's log said "Using config directory:
/// C:\WINDOWS\system32\config\systemprofile\AppData\Roaming\qBittorrent". Qt asks Windows for the Roaming folder and ignores the APPDATA the
/// service sets, so it opened a fresh profile (Web UI off, no login) instead of the one the install set up, and nothing answered on 8081.
/// So the service names the settings qBittorrent reads on the command line.
/// SABnzbd is different (checked on the VM with a SYSTEM task running SABnzbd-console.exe): in session 0 it stops at once in
/// handle_windows_service ("StartServiceCtrlDispatcher ... could not connect to the service controller"), as SYSTEM and as the person's
/// own account alike, while the same command in the signed-in session listens on 8080 and reads the right ini. A different argument
/// changes nothing, so its launch is left as it is.
/// </summary>
public sealed class ServiceClientLaunchTests
{
    private const string DataRoot = @"C:\Users\sam\AppData\Local\DelunoData";

    [Fact]
    public void QBittorrent_is_started_with_a_profile_folder_of_its_own_seeded_from_the_settings_the_install_wrote()
    {
        var plan = ServiceClientLaunch.Plan(@"C:\Program Files\qBittorrent\qbittorrent.exe", null, DataRoot);

        Assert.Equal(@"--profile=C:\Users\sam\AppData\Local\DelunoData\service-profiles\qbittorrent", plan.Arguments);
        Assert.Equal(@"C:\Users\sam\AppData\Roaming\qBittorrent\qBittorrent.ini", plan.SeedFrom);
        Assert.Equal(@"C:\Users\sam\AppData\Local\DelunoData\service-profiles\qbittorrent\qBittorrent\config\qBittorrent.ini", plan.SeedTo);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SABnzbd\SABnzbd.exe", "--server 127.0.0.1:8080 --browser 0")]
    [InlineData(@"C:\Program Files\NZBGet\nzbget.exe", "-s -o ControlIP=127.0.0.1")]
    [InlineData(@"C:\Program Files\Transmission\transmission-daemon.exe", "-f -r 127.0.0.1")]
    public void The_other_clients_are_left_exactly_as_they_were(string path, string arguments)
    {
        var plan = ServiceClientLaunch.Plan(path, arguments, DataRoot);

        Assert.Equal(arguments, plan.Arguments);
        Assert.Null(plan.SeedFrom);
    }

    [Fact]
    public void A_data_folder_outside_a_profile_still_gives_qBittorrent_a_profile_of_its_own_with_nothing_to_copy()
    {
        var plan = ServiceClientLaunch.Plan(@"C:\Program Files\qBittorrent\qbittorrent.exe", null, @"D:\Media\DelunoData");

        Assert.Equal(@"--profile=D:\Media\DelunoData\service-profiles\qbittorrent", plan.Arguments);
        Assert.Null(plan.SeedFrom);
    }

    [Theory]
    [InlineData(null, null, false)]                                    // the person has no settings file: nothing to copy
    [InlineData("2026-11-03T10:00:00", null, true)]                    // the service profile has none yet
    [InlineData("2026-11-03T10:00:00", "2026-11-03T09:00:00", true)]   // the person changed a setting in tray mode since the last service start
    [InlineData("2026-11-03T09:00:00", "2026-11-03T10:00:00", false)]  // the service profile is newer: its own changes are kept
    [InlineData("2026-11-03T10:00:00", "2026-11-03T10:00:00", false)]
    public void The_persons_settings_are_copied_at_each_service_start_when_they_are_newer_than_the_service_profiles(string? person, string? profile, bool copy)
    {
        static DateTime? Parse(string? t) => t is null ? null : DateTime.Parse(t, CultureInfo.InvariantCulture);

        Assert.Equal(copy, ServiceClientLaunch.ShouldSeed(Parse(person), Parse(profile)));
    }

    [Fact]
    public void The_service_starter_applies_the_plan_before_it_starts_a_client()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "ServiceProcessStarter.cs")))
        {
            dir = dir.Parent;
        }

        var source = File.ReadAllText(Path.Combine(dir!.FullName, "apps", "windows-tray", "ServiceProcessStarter.cs"));
        Assert.Contains("ServiceClientLaunch.Plan(", source, StringComparison.Ordinal);
        Assert.Contains("ServiceClientLaunch.ShouldSeed(", source, StringComparison.Ordinal);
    }
}
