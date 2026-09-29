using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1052: Transmission 4.1.1 MSI feature table, the daemon start and the service it registers.</summary>
public sealed partial class ComponentSetupTests
{
    // ---- #1052: Transmission -------------------------------------------------

    [Fact]
    public void Transmission_installs_the_daemon_feature_the_msi_leaves_out_by_default()
    {
        // The 4.1.1 MSI's Feature table (read with the WindowsInstaller COM object):
        // CommonLibs, CompleteInstall, QtClient (level 1), Daemon, CmdTools, WebUi
        // (level 2). transmission-daemon.exe is in Daemon's component, so a default
        // install (level 1) leaves it out.
        var transmission = Load("transmission");

        Assert.Contains("ADDLOCAL=CommonLibs,Daemon,WebUi", transmission.SilentArgs);
        Assert.Equal("Transmission", transmission.DisableServiceAfterInstall);
    }

    [Fact]
    public void Transmission_runs_the_daemon_in_the_foreground_with_no_window_from_a_config_folder_bound_locally()
    {
        var transmission = Load("transmission");

        Assert.True(transmission.StartHidden);
        Assert.Contains("-f", transmission.PostInstallArgs!.Split(' '));
        Assert.Contains("-g", transmission.PostInstallArgs.Split(' '));
        Assert.Contains("-r 127.0.0.1", transmission.PostInstallArgs);
        Assert.Contains("\"rpc-bind-address\": \"127.0.0.1\"", transmission.ConfigFileContent);
        Assert.Contains("\"rpc-port\": 9091", transmission.ConfigFileContent);
        Assert.StartsWith("%LocalAppData%\\Deluno\\components\\transmission", transmission.ConfigFilePath);
    }

    [Fact]
    public void The_service_script_installs_then_stops_and_disables_the_service_only_when_the_install_worked()
    {
        var script = ElevatedInstallScript.Build("C:\\t\\tr.msi", "ABC123", "/qn ADDLOCAL=CommonLibs,Daemon,WebUi", "Transmission");
        var lines = script.Split("\n");

        var msi = Array.FindIndex(lines, l => l.Contains("Start-Process -FilePath 'msiexec.exe'", StringComparison.Ordinal));
        var check = Array.FindIndex(lines, l => l.StartsWith("if ($code -ne 0) { exit $code }", StringComparison.Ordinal));
        var stop = Array.FindIndex(lines, l => l.Contains("Stop-Service -Name 'Transmission'", StringComparison.Ordinal));
        Assert.True(msi >= 0 && check > msi && stop > check, "install, then check the result, then stop the service");
        Assert.Contains("ALLUSERS=1", script);
        Assert.Contains("ADDLOCAL=CommonLibs,Daemon,WebUi", script);
        Assert.Contains("Set-Service -Name 'Transmission' -StartupType Disabled", script);
    }

    [Fact]
    public async Task A_transmission_style_msi_is_installed_in_one_elevated_step_that_also_disables_its_service()
    {
        var download = WriteTempFile(new byte[20], ".msi");
        var runner = new ElevatedRecordingRunner();
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(download), new AlwaysSigned(), runner,
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry());

        var spec = Load("transmission") with { Sha256 = null, MustBeSigned = true, MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        var result = await service.InstallAsync(spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Equal(1, runner.ElevatedCalls);
        Assert.Equal(0, runner.PlainCalls);
        Assert.Equal("powershell.exe", runner.LastPath);
        Assert.Contains("-EncodedCommand", runner.LastArguments);
        Assert.False(File.Exists(download + ".install.cmd"), "no script file is left in the user's temp folder to be swapped");
    }

}
