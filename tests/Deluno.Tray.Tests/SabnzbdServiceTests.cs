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
/// Deluno only ever removes a service it made itself (a marker in its data folder), and a SABnzbd that is running is asked to shut down
/// through its API and waited for before anything forces it: it may be mid-download or mid-unpack.
/// </summary>
public sealed partial class ComponentSetupTests
{
    private const string SabInstall = @"C:\Program Files\SABnzbd";
    private const string SabIni = @"C:\Users\sam\AppData\Local\sabnzbd\sabnzbd.ini";
    private const string SabWait = "waited until SABnzbd's process exited and port 8080 closed";

    /// <summary>Everything the plan looks at or does besides running commands, kept in the order it happens.</summary>
    private sealed class FakeSabnzbdEnvironment : ISabnzbdServiceEnvironment
    {
        public List<string> Events { get; } = [];
        public bool Exists { get; set; }
        public string? Mode { get; set; } = "Auto";
        public bool Installed { get; set; } = true;
        public bool Marker { get; set; }
        public bool Running { get; set; }
        public bool StopsWhenAsked { get; set; } = true;
        public bool ServiceExists() => Exists;
        public string? StartMode() => Mode;
        public bool FileExists(string path) => Installed && path.StartsWith(SabInstall, StringComparison.OrdinalIgnoreCase);
        public bool MarkerExists() => Marker;
        public void WriteMarker() { Marker = true; Events.Add("marker written"); }
        public void DeleteMarker() { Marker = false; Events.Add("marker deleted"); }
        public bool SabnzbdRunning() => Running;
        public bool AskToShutDown() { Events.Add("asked SABnzbd to shut down"); return true; }
        public bool WaitUntilStopped() { Events.Add(SabWait); return StopsWhenAsked; }
        public void ForceStop() => Events.Add("force-stopped SABnzbd");
        public void Log(string message) => Events.Add("log: " + message);
    }

    private sealed class RecordingCommands(FakeSabnzbdEnvironment? env = null) : IServiceCommandRunner
    {
        public List<string> Lines { get; } = [];

        public void Run(string fileName, string arguments)
        {
            Lines.Add($"{fileName} {arguments}");
            env?.Events.Add($"ran {fileName} {arguments}");
        }
    }

    private static SabnzbdService Sab(RecordingCommands runner, FakeSabnzbdEnvironment env) => new(runner, env, SabInstall, SabIni);

    private static string SabInstallLine => $@"{SabInstall}\SABnzbd-console.exe -f ""{SabIni}"" install";

    [Fact]
    public void Choosing_to_run_as_a_Windows_service_registers_and_starts_SABnzbds_own_service_on_the_persons_settings()
    {
        var env = new FakeSabnzbdEnvironment();
        var runner = new RecordingCommands(env);

        Assert.True(Sab(runner, env).Register());

        Assert.Equal([SabInstallLine, "sc.exe config SABnzbd start= auto", "sc.exe start SABnzbd"], runner.Lines);
        Assert.True(env.Marker);
    }

    [Fact]
    public void The_marker_is_written_when_Deluno_runs_the_install_and_not_when_it_leaves_a_service_alone()
    {
        var env = new FakeSabnzbdEnvironment();
        Sab(new RecordingCommands(env), env).Register();
        var install = env.Events.FindIndex(e => e.EndsWith(" install", StringComparison.Ordinal));
        Assert.True(install >= 0 && env.Events.IndexOf("marker written") > install);

        var own = new FakeSabnzbdEnvironment { Exists = true };
        Sab(new RecordingCommands(own), own).Register();
        Assert.DoesNotContain("marker written", own.Events);
    }

    [Fact]
    public void A_service_Deluno_made_is_removed_before_it_is_registered_again()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Marker = true };
        var runner = new RecordingCommands(env);

        Assert.True(Sab(runner, env).Register());

        Assert.Equal("sc.exe stop SABnzbd", runner.Lines[0]);
        Assert.Equal("sc.exe delete SABnzbd", runner.Lines[1]);
        Assert.Contains(SabInstallLine, runner.Lines);
    }

    [Fact]
    public void A_SABnzbd_service_the_person_already_has_is_left_alone_and_kept_starting_automatically()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Marker = false, Mode = "Manual" };
        var runner = new RecordingCommands(env);

        Assert.True(Sab(runner, env).Register());

        // No second service, no install, no stop or delete, no marker, no force-stop; only the start type is put right, and it is said.
        Assert.Equal(["sc.exe config SABnzbd start= auto"], runner.Lines);
        Assert.False(env.Marker);
        Assert.Contains(env.Events, e => e.StartsWith("log: ", StringComparison.Ordinal) && e.Contains("your own SABnzbd service", StringComparison.Ordinal) && e.Contains("automatically", StringComparison.Ordinal));
    }

    [Fact]
    public void A_persons_own_service_that_already_starts_automatically_needs_no_change()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Mode = "Auto", Running = true };
        var runner = new RecordingCommands(env);

        Assert.True(Sab(runner, env).Register());

        Assert.Empty(runner.Lines);
        Assert.DoesNotContain(env.Events, e => e.StartsWith("force", StringComparison.Ordinal) || e.StartsWith("asked", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_SABnzbd_installed_nothing_is_registered()
    {
        var env = new FakeSabnzbdEnvironment { Installed = false };
        var runner = new RecordingCommands(env);

        Assert.False(Sab(runner, env).Register());
        Assert.Empty(runner.Lines);
    }

    [Fact]
    public void Going_back_to_the_tray_removes_the_service_Deluno_made_and_its_marker()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Marker = true };
        var runner = new RecordingCommands(env);

        ServiceManager.ApplySabnzbd(ServiceMode.Tray, Sab(runner, env));

        Assert.Equal(["sc.exe stop SABnzbd", "sc.exe delete SABnzbd"], runner.Lines);
        Assert.False(env.Marker);
    }

    [Fact]
    public void Going_back_to_the_tray_never_removes_a_service_Deluno_did_not_make()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Marker = false };
        var runner = new RecordingCommands(env);

        ServiceManager.ApplySabnzbd(ServiceMode.Tray, Sab(runner, env));

        Assert.Empty(runner.Lines);
    }

    [Theory]
    [InlineData(ServiceMode.ServiceLocalSystem)]
    [InlineData(ServiceMode.ServiceRunAsUser)]
    public void Each_service_mode_registers_SABnzbds_service(ServiceMode mode)
    {
        var env = new FakeSabnzbdEnvironment();
        var runner = new RecordingCommands(env);

        ServiceManager.ApplySabnzbd(mode, Sab(runner, env));

        Assert.Contains("sc.exe start SABnzbd", runner.Lines);
        Assert.DoesNotContain(runner.Lines, l => l.StartsWith("sc.exe delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_a_service_that_is_not_there_runs_nothing_and_clears_a_stale_marker()
    {
        var env = new FakeSabnzbdEnvironment { Exists = false, Marker = true };
        var runner = new RecordingCommands(env);

        Assert.False(Sab(runner, env).Remove());
        Assert.Empty(runner.Lines);
        Assert.False(env.Marker);
    }

    [Fact]
    public void A_running_SABnzbd_is_asked_to_shut_down_and_waited_for_before_anything_forces_it()
    {
        var env = new FakeSabnzbdEnvironment { Running = true, StopsWhenAsked = true };

        Sab(new RecordingCommands(env), env).Register();

        var ask = env.Events.IndexOf("asked SABnzbd to shut down");
        var wait = env.Events.IndexOf(SabWait);
        var install = env.Events.FindIndex(e => e.EndsWith(" install", StringComparison.Ordinal));
        Assert.True(ask >= 0 && wait > ask && install > wait);
        Assert.DoesNotContain("force-stopped SABnzbd", env.Events);
    }

    [Fact]
    public void SABnzbd_is_force_stopped_only_after_the_wait_ran_out()
    {
        var env = new FakeSabnzbdEnvironment { Running = true, StopsWhenAsked = false };
        var runner = new RecordingCommands(env);

        Sab(runner, env).Register();

        var wait = env.Events.IndexOf(SabWait);
        var force = env.Events.IndexOf("force-stopped SABnzbd");
        Assert.True(wait >= 0 && force > wait);
        Assert.DoesNotContain(runner.Lines, l => l.StartsWith("taskkill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_SABnzbd_that_is_not_running_is_not_asked_or_forced()
    {
        var env = new FakeSabnzbdEnvironment { Running = false };

        Sab(new RecordingCommands(env), env).Register();

        Assert.DoesNotContain(env.Events, e => e.StartsWith("asked", StringComparison.Ordinal) || e.StartsWith("force", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shutdown_request_uses_the_persons_API_key_and_the_host_and_port_in_their_settings()
    {
        Assert.Equal("http://127.0.0.1:8085/api?mode=shutdown&apikey=abc123def", SabnzbdProcessControl.ShutdownUrl("[misc]\r\nhost = 127.0.0.1\r\nport = 8085\r\napi_key = abc123def\r\n"));
        Assert.Equal("http://127.0.0.1:8080/api?mode=shutdown&apikey=k", SabnzbdProcessControl.ShutdownUrl("api_key = k\r\n"));
        Assert.Null(SabnzbdProcessControl.ShutdownUrl("[misc]\r\nhost = 127.0.0.1\r\n"));
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
    public void Deluno_uninstalling_removes_the_SABnzbd_service_it_made_and_leaves_one_it_did_not_make()
    {
        var env = new FakeSabnzbdEnvironment { Exists = true, Marker = true };
        var runner = new RecordingCommands(env);

        InstallHooks.OnBeforeUninstall(() => throw new InvalidOperationException("no firewall"), new NoSignInEntry(), Sab(runner, env));

        Assert.Equal(["sc.exe stop SABnzbd", "sc.exe delete SABnzbd"], runner.Lines);

        var own = new FakeSabnzbdEnvironment { Exists = true, Marker = false };
        var ownRunner = new RecordingCommands(own);
        InstallHooks.OnBeforeUninstall(() => throw new InvalidOperationException("no firewall"), new NoSignInEntry(), Sab(ownRunner, own));
        Assert.Empty(ownRunner.Lines);
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
