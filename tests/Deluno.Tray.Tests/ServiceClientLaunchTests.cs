using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1452, from the VM after a restart with the service running (rc.30.37 candidate, 3 Nov): qBittorrent's log said "Using config directory:
/// C:\WINDOWS\system32\config\systemprofile\AppData\Roaming\qBittorrent". Qt asks Windows for the Roaming folder and ignores the APPDATA the
/// service sets, so it opened a fresh profile (Web UI off, no login) instead of the one the install set up, and nothing answered on 8081.
/// SABnzbd's process ran with no file written and nothing listening on 8080 though its settings file (127.0.0.1:8080) is in the person's folder.
/// So the service names the settings each client reads on the command line.
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

    [Fact]
    public void SABnzbd_is_started_with_the_settings_file_the_install_set_up()
    {
        var plan = ServiceClientLaunch.Plan(@"C:\Program Files\SABnzbd\SABnzbd.exe", "--server 127.0.0.1:8080 --browser 0", DataRoot);

        Assert.Equal(@"--server 127.0.0.1:8080 --browser 0 -f ""C:\Users\sam\AppData\Local\sabnzbd\sabnzbd.ini""", plan.Arguments);
        Assert.Null(plan.SeedFrom);
    }

    [Theory]
    [InlineData(@"C:\Program Files\NZBGet\nzbget.exe", "-s -o ControlIP=127.0.0.1")]
    [InlineData(@"C:\Program Files\Transmission\transmission-daemon.exe", "-f -r 127.0.0.1")]
    public void The_clients_that_already_start_are_left_exactly_as_they_were(string path, string arguments)
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

    [Fact]
    public void The_service_starter_applies_the_plan_before_it_starts_a_client()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "ServiceProcessStarter.cs")))
        {
            dir = dir.Parent;
        }

        Assert.Contains("ServiceClientLaunch.Plan(", File.ReadAllText(Path.Combine(dir!.FullName, "apps", "windows-tray", "ServiceProcessStarter.cs")), StringComparison.Ordinal);
    }
}
