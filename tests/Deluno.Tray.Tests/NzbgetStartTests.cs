using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1051: NZBGet starts in server mode without a window; a client Deluno started restarts at sign-in.</summary>
public sealed partial class ComponentSetupTests
{
    // ---- #1051: NZBGet -------------------------------------------------------

    // The four lines of nzbget.conf.template (NZBGet 21.1's Windows installer) the seed edits.
    private const string NzbgetTemplateLines =
        "MainDir=${AppDir}\\downloads\r\nControlIP=0.0.0.0\r\nControlPort=6789\r\nControlUsername=nzbget\r\nControlPassword=tegbzn6789\r\n";

    [Fact]
    public void Nzbget_is_started_in_server_mode_with_no_window_and_never_with_the_service_switch()
    {
        // In NZBGet 21.1's Windows build '-D' hands the process to the service
        // dispatcher (daemon/main/nzbget.cpp) and crashes when run by hand
        // (0xC0000005); '-s' is its console-mode server.
        var nzbget = Load("nzbget");

        Assert.DoesNotContain("-D", nzbget.PostInstallArgs!.Split(' '));
        Assert.Contains("-s", nzbget.PostInstallArgs.Split(' '));
        Assert.True(nzbget.StartHidden);
    }

    [Fact]
    public void Nzbget_config_is_built_from_its_own_template_bound_locally_and_keeps_its_own_login()
    {
        var nzbget = Load("nzbget");
        Assert.Equal("%ProgramData%\\NZBGet\\nzbget.conf", nzbget.ConfigFilePath);
        Assert.Equal("%ProgramFiles%\\NZBGet\\nzbget.conf.template", nzbget.ConfigTemplateSourcePath);

        var config = ComponentConfig.ApplyReplacements(NzbgetTemplateLines, nzbget.ConfigReplacements!, null);

        Assert.NotNull(config);
        Assert.Contains("ControlIP=127.0.0.1", config);
        Assert.DoesNotContain("0.0.0.0", config);
        // The login stays as NZBGet ships it (#1061); Deluno connects with that default.
        Assert.Contains("ControlUsername=nzbget", config);
        Assert.Contains("ControlPassword=tegbzn6789", config);
        Assert.DoesNotContain("${AppDir}", config);
        Assert.Contains("ControlPort=6789", config);
    }

    [Fact]
    public void A_template_that_no_longer_has_a_line_the_seed_edits_is_not_seeded()
    {
        var replacements = new Dictionary<string, string> { ["ControlIP=0.0.0.0"] = "ControlIP=127.0.0.1" };

        Assert.Null(ComponentConfig.ApplyReplacements("ControlIP=192.168.0.5\r\n", replacements, null));
    }

    [Fact]
    public async Task The_nzbget_install_seeds_from_the_installed_template_and_starts_it_hidden_and_again_after_sign_in()
    {
        var store = new RecordingCredentialStore();
        var written = new FakeConfigWriter(result: true);
        var starter = new RecordingStarter();
        var registry = new FakeRegistry();

        var result = await InstallWith(Load("nzbget"), written, store, template: NzbgetTemplateLines, starter: starter, registry: registry);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Contains("ControlIP=127.0.0.1", written.Content);
        Assert.False(store.Has("nzbget"));
        Assert.Empty(store.Saved);
        Assert.True(starter.LastHidden);
        Assert.Contains("nzbget", registry.Kinds);
    }

    // ---- sign-in restart -----------------------------------------------------

    [Fact]
    public async Task A_client_Deluno_started_is_started_again_when_it_is_not_answering()
    {
        var starter = new RecordingStarter();
        var auto = new ComponentAutoStarter(new FakeRegistry("nzbget", "transmission"), new FakePorts(), starter);

        var started = await auto.RunAsync(BundledComponents.Load(), CancellationToken.None);

        Assert.Equal(new[] { "nzbget", "transmission" }, started.OrderBy(k => k).ToArray());
        Assert.All(starter.Calls, call => Assert.True(call.Hidden));
        Assert.Contains(starter.Calls, c => c.Path.EndsWith("nzbget.exe", StringComparison.OrdinalIgnoreCase) && c.Args!.Contains("-s"));
        Assert.Contains(starter.Calls, c => c.Path.EndsWith("transmission-daemon.exe", StringComparison.OrdinalIgnoreCase) && c.Args!.Contains("-f"));
        // The config folder in the arguments is expanded, not a literal %LocalAppData%.
        Assert.All(starter.Calls, c => Assert.DoesNotContain("%", c.Args));
    }

    [Fact]
    public async Task A_client_that_is_already_answering_or_was_not_started_by_Deluno_is_left_alone()
    {
        var starter = new RecordingStarter();
        var auto = new ComponentAutoStarter(new FakeRegistry("nzbget"), new FakePorts(open: 6789), starter);

        Assert.Empty(await auto.RunAsync(BundledComponents.Load(), CancellationToken.None));
        Assert.Empty(starter.Calls);

        var none = new ComponentAutoStarter(new FakeRegistry(), new FakePorts(), starter);
        Assert.Empty(await none.RunAsync(BundledComponents.Load(), CancellationToken.None));
        Assert.Empty(starter.Calls);
    }

    [Fact]
    public async Task Weir_is_never_started_by_the_sign_in_restart()
    {
        var starter = new RecordingStarter();
        var auto = new ComponentAutoStarter(new FakeRegistry("weir"), new FakePorts(), starter);

        Assert.Empty(await auto.RunAsync(BundledComponents.Load(), CancellationToken.None));
    }

}
