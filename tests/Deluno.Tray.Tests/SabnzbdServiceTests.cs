using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1452: SABnzbd cannot run as a plain process in session 0 (it stops in its own Windows-service check whatever the arguments), so when Deluno
/// runs as a Windows service the client is run as SABnzbd's own Windows service. Proven by hand on the golden VM (3 Nov 2026), run from the
/// signed-in session with the console exe:
///   SABnzbd-console.exe -f "C:\Users\sam\AppData\Local\sabnzbd\sabnzbd.ini" install   -> "Service installed" (service SABnzbd, LocalSystem)
///   sc config SABnzbd start= auto ; sc start SABnzbd                                 -> Running, 127.0.0.1:8080 LISTENING, the person's API key and folders answer
/// and again after a restart of the computer.
/// </summary>
public sealed partial class ComponentSetupTests
{
    private const string SabInstall = @"C:\Program Files\SABnzbd";
    private const string SabIni = @"C:\Users\sam\AppData\Local\sabnzbd\sabnzbd.ini";

    private sealed class RecordingCommands : IServiceCommandRunner
    {
        public List<string> Lines { get; } = [];
        public void Run(string fileName, string arguments) => Lines.Add($"{fileName} {arguments}");
    }

    private static SabnzbdService Sab(RecordingCommands runner, bool registered = false, bool installed = true)
        => new(runner, () => registered, path => installed && path.StartsWith(SabInstall, StringComparison.OrdinalIgnoreCase), SabInstall, SabIni);

    [Fact]
    public void Choosing_to_run_as_a_Windows_service_registers_and_starts_SABnzbds_own_service_on_the_persons_settings()
    {
        var runner = new RecordingCommands();

        Assert.True(Sab(runner).Register());

        Assert.Equal(
        [
            "taskkill.exe /F /IM SABnzbd.exe /IM SABnzbd-console.exe",
            $@"{SabInstall}\SABnzbd-console.exe -f ""{SabIni}"" install",
            "sc.exe config SABnzbd start= auto",
            "sc.exe start SABnzbd"
        ], runner.Lines);
    }

    [Fact]
    public void A_service_that_is_already_there_is_removed_before_it_is_registered_again()
    {
        var runner = new RecordingCommands();

        Assert.True(Sab(runner, registered: true).Register());

        Assert.Equal("sc.exe stop SABnzbd", runner.Lines[0]);
        Assert.Equal("sc.exe delete SABnzbd", runner.Lines[1]);
        Assert.Contains($@"{SabInstall}\SABnzbd-console.exe -f ""{SabIni}"" install", runner.Lines);
    }

    [Fact]
    public void Without_SABnzbd_installed_nothing_is_registered()
    {
        var runner = new RecordingCommands();

        Assert.False(Sab(runner, installed: false).Register());
        Assert.Empty(runner.Lines);
    }

    [Fact]
    public void Going_back_to_the_tray_removes_the_service_and_does_not_start_SABnzbd_itself()
    {
        var runner = new RecordingCommands();

        ServiceManager.ApplySabnzbd(ServiceMode.Tray, Sab(runner, registered: true));

        Assert.Equal(["sc.exe stop SABnzbd", "sc.exe delete SABnzbd"], runner.Lines);
    }

    [Theory]
    [InlineData(ServiceMode.ServiceLocalSystem)]
    [InlineData(ServiceMode.ServiceRunAsUser)]
    public void Each_service_mode_registers_SABnzbds_service(ServiceMode mode)
    {
        var runner = new RecordingCommands();

        ServiceManager.ApplySabnzbd(mode, Sab(runner));

        Assert.Contains("sc.exe start SABnzbd", runner.Lines);
        Assert.DoesNotContain(runner.Lines, l => l.StartsWith("sc.exe delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_a_service_that_is_not_there_runs_nothing()
    {
        var runner = new RecordingCommands();

        Assert.False(Sab(runner, registered: false).Remove());
        Assert.Empty(runner.Lines);
    }

    [Fact]
    public void The_uninstall_service_command_and_Delunos_uninstall_both_remove_the_service()
    {
        var root = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "apps", "windows-tray", "ServiceManager.cs"));
        var hooks = File.ReadAllText(Path.Combine(root, "apps", "windows-tray", "InstallHooks.cs"));

        // Uninstall() is what --uninstall-service runs.
        var uninstall = program[program.IndexOf("public static void Uninstall()", StringComparison.Ordinal)..];
        Assert.Contains("SabnzbdService", uninstall[..uninstall.IndexOf('}', StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("SabnzbdService", hooks[hooks.IndexOf("internal static void OnBeforeUninstall(", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void Deluno_uninstalling_removes_SABnzbds_service_even_when_the_firewall_rule_cannot_be_looked_for()
    {
        var runner = new RecordingCommands();

        InstallHooks.OnBeforeUninstall(() => throw new InvalidOperationException("no firewall"), new NoSignInEntry(), Sab(runner, registered: true));

        Assert.Equal(["sc.exe stop SABnzbd", "sc.exe delete SABnzbd"], runner.Lines);
    }

    private sealed class NoSignInEntry : ISignInEntry
    {
        public bool IsEnabled() => false;
        public void Enable() { }
        public void Disable() { }
    }

    [Fact]
    public async Task While_Deluno_is_a_service_it_starts_the_other_clients_but_not_a_second_SABnzbd()
    {
        var registry = new FakeRegistry("sabnzbd", "nzbget");
        var starter = new RecordingStarter();

        var started = await ServiceHost.StartComponentsAsync(registry, new FakePorts(), starter, null, CancellationToken.None, sabnzbdRunsAsService: true);

        Assert.Equal(["nzbget"], started.ToArray());
        Assert.DoesNotContain(starter.Calls, c => c.Path.Contains("SABnzbd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_SABnzbds_service_the_service_host_still_starts_SABnzbd_as_before()
    {
        var starter = new RecordingStarter();

        var started = await ServiceHost.StartComponentsAsync(new FakeRegistry("sabnzbd"), new FakePorts(), starter, null, CancellationToken.None, sabnzbdRunsAsService: false);

        Assert.Equal(["sabnzbd"], started.ToArray());
    }

    [Fact]
    public async Task The_tray_starts_SABnzbd_as_it_always_did()
    {
        var starter = new RecordingStarter();

        var started = await new ComponentAutoStarter(new FakeRegistry("sabnzbd"), new FakePorts(), starter).RunAsync(BundledComponents.Load(), CancellationToken.None);

        Assert.Equal(["sabnzbd"], started.ToArray());
    }

    [Fact]
    public void The_service_host_asks_whether_SABnzbds_service_is_there_before_it_starts_the_clients()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "apps", "windows-tray", "ServiceHost.cs"));
        Assert.Contains("sabnzbdRunsAsService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persons_settings_file_is_found_from_the_data_folder()
        => Assert.Equal(SabIni, SabnzbdService.IniPathFor(@"C:\Users\sam\AppData\Local\DelunoData"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "ServiceHost.cs")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
