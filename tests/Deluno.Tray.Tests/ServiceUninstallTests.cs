using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1462: uninstalling a service-mode Deluno left the Windows service behind, stopped and pointing at the deleted program. The hook
/// now asks Windows once (one elevated run of <c>--uninstall-service</c>, which removes Deluno's service and the SABnzbd service only
/// if Deluno made it); a declined prompt leaves a plain line in the uninstall log; tray mode is never asked anything.
/// </summary>
public sealed class ServiceUninstallTests
{
    private sealed class FakeEnvironment : IServiceUninstallEnvironment
    {
        public List<string> Log { get; } = [];
        public int Prompts { get; private set; }
        public bool Deluno { get; set; }
        public bool SabnzbdMadeByDeluno { get; set; }
        public bool PersonAllows { get; set; } = true;
        public bool RunThrows { get; set; }

        public bool DelunoServiceExists() => Deluno;
        public bool SabnzbdServiceMadeByDeluno() => SabnzbdMadeByDeluno;

        public bool RunElevatedUninstallService()
        {
            Prompts++;
            if (RunThrows)
            {
                throw new InvalidOperationException("could not start");
            }

            if (PersonAllows)
            {
                Deluno = false;
                SabnzbdMadeByDeluno = false;
            }

            return PersonAllows;
        }

        void IServiceUninstallEnvironment.Log(string message) => Log.Add(message);
    }

    [Fact]
    public void A_service_mode_uninstall_asks_once_and_leaves_no_Deluno_service()
    {
        var env = new FakeEnvironment { Deluno = true };

        ServiceUninstall.Run(env);

        Assert.Equal(1, env.Prompts);
        Assert.False(env.Deluno);
    }

    [Fact]
    public void A_service_mode_uninstall_with_a_SABnzbd_service_Deluno_made_asks_once_and_removes_both()
    {
        var env = new FakeEnvironment { Deluno = true, SabnzbdMadeByDeluno = true };

        ServiceUninstall.Run(env);

        Assert.Equal(1, env.Prompts);
        Assert.False(env.Deluno);
        Assert.False(env.SabnzbdMadeByDeluno);
    }

    [Fact]
    public void A_tray_mode_uninstall_shows_no_prompt()
    {
        var env = new FakeEnvironment();

        ServiceUninstall.Run(env);

        Assert.Equal(0, env.Prompts);
    }

    [Fact]
    public void A_SABnzbd_service_the_person_made_is_not_a_reason_to_ask()
    {
        // SabnzbdMadeByDeluno stays false: the person's own SABnzbd service is not ours, so there is nothing to remove or ask about.
        var env = new FakeEnvironment { Deluno = false, SabnzbdMadeByDeluno = false };

        ServiceUninstall.Run(env);

        Assert.Equal(0, env.Prompts);
    }

    [Fact]
    public void A_SABnzbd_service_Deluno_made_alone_is_still_removed_with_one_prompt()
    {
        var env = new FakeEnvironment { SabnzbdMadeByDeluno = true };

        ServiceUninstall.Run(env);

        Assert.Equal(1, env.Prompts);
        Assert.False(env.SabnzbdMadeByDeluno);
    }

    [Fact]
    public void A_declined_prompt_names_the_leftover_service_and_the_command_that_removes_it()
    {
        var env = new FakeEnvironment { Deluno = true, PersonAllows = false };

        ServiceUninstall.Run(env);

        Assert.Equal(1, env.Prompts);
        var line = Assert.Single(env.Log);
        Assert.Contains("'Deluno'", line, StringComparison.Ordinal);
        Assert.Contains("sc.exe delete Deluno", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declined_prompt_names_each_leftover_service()
    {
        var env = new FakeEnvironment { Deluno = true, SabnzbdMadeByDeluno = true, PersonAllows = false };

        ServiceUninstall.Run(env);

        Assert.Contains(env.Log, l => l.Contains("sc.exe delete Deluno", StringComparison.Ordinal));
        Assert.Contains(env.Log, l => l.Contains("sc.exe delete SABnzbd", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removal_that_worked_says_so_and_names_no_command()
    {
        var env = new FakeEnvironment { Deluno = true };

        ServiceUninstall.Run(env);

        Assert.DoesNotContain(env.Log, l => l.Contains("sc.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_prompt_that_cannot_be_shown_never_stops_the_uninstall_and_still_says_what_is_left()
    {
        var env = new FakeEnvironment { Deluno = true, RunThrows = true };

        var exception = Record.Exception(() => ServiceUninstall.Run(env));

        Assert.Null(exception);
        Assert.Contains(env.Log, l => l.Contains("sc.exe delete Deluno", StringComparison.Ordinal));
    }

    [Fact]
    public void The_uninstall_hook_runs_the_service_removal()
    {
        var env = new FakeEnvironment { Deluno = true };

        InstallHooks.OnBeforeUninstall(() => throw new InvalidOperationException("no firewall"), new NoSignInEntry(), env);

        Assert.Equal(1, env.Prompts);
    }

    private sealed class NoSignInEntry : ISignInEntry
    {
        public bool IsEnabled() => false;
        public void Enable() { }
        public void Disable() { }
    }
}
