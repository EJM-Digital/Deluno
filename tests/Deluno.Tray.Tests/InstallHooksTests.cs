using System.Reflection;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// Velopack kills the install/update hooks after 30 seconds. A clean-VM run
/// under an elevated Administrator once blew that budget anyway: 18 seconds
/// of cold start before the first log line, then the elevated firewall step
/// (<c>netsh</c>) pushed it past the limit and Velopack killed the hook
/// (#968). <see cref="InstallHooks.OnAfterInstall"/> -- wired to both the
/// install and the update fast callback -- now never runs the firewall step,
/// elevated or not: it logs one line and returns. The rule still gets added,
/// just never from this hook: <see cref="InstallHooks.OnFirstRun"/> adds it
/// after a fresh install, and the tray offers (and an update ends up with it
/// through) the same step on the first normal start after an update, which
/// has no first run of its own.
/// </summary>
public sealed class InstallHooksTests
{
    [Fact]
    public void OnAfterInstall_not_elevated_never_touches_the_firewall_factory()
    {
        var firewallFactoryCalled = false;
        InstallHooks.IsElevatedCheck = () => false;
        InstallHooks.FirewallFactory = () =>
        {
            firewallFactoryCalled = true;
            throw new InvalidOperationException(
                "The install/update fast callback must never touch DelunoServer/AppSettings.");
        };

        try
        {
            InstallHooks.OnAfterInstall();
        }
        finally
        {
            ResetSeams();
        }

        Assert.False(firewallFactoryCalled);
    }

    [Fact]
    public void OnAfterInstall_elevated_also_never_touches_the_firewall_factory()
    {
        var firewallFactoryCalled = false;
        InstallHooks.IsElevatedCheck = () => true;
        InstallHooks.FirewallFactory = () =>
        {
            firewallFactoryCalled = true;
            throw new InvalidOperationException(
                "The install/update fast callback must never touch DelunoServer/AppSettings, even when elevated.");
        };

        try
        {
            InstallHooks.OnAfterInstall();
        }
        finally
        {
            ResetSeams();
        }

        Assert.False(firewallFactoryCalled);
    }

    [Fact]
    public void OnAfterInstall_never_throws()
    {
        InstallHooks.IsElevatedCheck = () => throw new InvalidOperationException("boom");
        InstallHooks.FirewallFactory = () => throw new InvalidOperationException("boom");

        try
        {
            var exception = Record.Exception(InstallHooks.OnAfterInstall);
            Assert.Null(exception);
        }
        finally
        {
            ResetSeams();
        }
    }

    // The golden VM's uninstall.keeps-data run (3 Nov 2026, scripts/golden-path/suite/vm/UpdatePaths.Lib.ps1) set Start with Windows, ran
    // the uninstall, and found the per-user Run entry still there, pointing at a Deluno.exe that was gone: "After the uninstall the
    // sign-in entry is still there (it was there before)". The hook's own words are "so nothing is left behind".
    [Fact]
    public void Uninstalling_clears_the_sign_in_entry_so_Windows_is_not_left_starting_a_program_that_is_gone()
    {
        var entry = new FakeSignInEntry(enabled: true);

        InstallHooks.OnBeforeUninstall(() => new WindowsFirewallLanAccess(7879, new NoRulesFirewall()), entry);

        Assert.False(entry.IsEnabled());
    }

    [Fact]
    public void Uninstalling_clears_the_sign_in_entry_when_there_is_no_firewall_rule_to_remove_too()
    {
        var entry = new FakeSignInEntry(enabled: true);
        var firewall = new NoRulesFirewall();

        InstallHooks.OnBeforeUninstall(() => new WindowsFirewallLanAccess(7879, firewall), entry);

        Assert.False(entry.IsEnabled());
        Assert.Empty(firewall.Calls);
    }

    [Fact]
    public void Uninstalling_with_no_sign_in_entry_changes_nothing_and_never_throws()
    {
        var entry = new FakeSignInEntry(enabled: false);

        var exception = Record.Exception(() => InstallHooks.OnBeforeUninstall(() => new WindowsFirewallLanAccess(7879, new NoRulesFirewall()), entry));

        Assert.Null(exception);
        Assert.Equal(0, entry.DisableCalls);
    }

    [Fact]
    public void An_entry_that_cannot_be_cleared_does_not_stop_the_firewall_rule_going_or_the_uninstall()
    {
        var entry = new FakeSignInEntry(enabled: true) { Throws = true };
        var firewall = new NoRulesFirewall();

        var exception = Record.Exception(() => InstallHooks.OnBeforeUninstall(() => new WindowsFirewallLanAccess(7879, firewall), entry));

        Assert.Null(exception);
    }

    private sealed class FakeSignInEntry(bool enabled) : ISignInEntry
    {
        public bool Throws { get; init; }
        public int DisableCalls { get; private set; }
        public bool IsEnabled() => enabled;
        public void Enable() => enabled = true;

        public void Disable()
        {
            DisableCalls++;
            if (Throws)
            {
                throw new UnauthorizedAccessException("The Run key refused the change.");
            }

            enabled = false;
        }
    }

    private sealed class NoRulesFirewall : IWindowsFirewall
    {
        public List<string> Calls { get; } = [];
        public bool IsElevated => true;
        public FirewallSnapshot? Read() => new(2, 0, []);

        public Task<FirewallCommandResult> RunNetshAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
        {
            Calls.Add(netshArguments);
            return Task.FromResult(new FirewallCommandResult(0, "Ok."));
        }
    }

    /// <summary>
    /// Restores the real, production seams so no other test in this assembly
    /// (which run in the same process) ever sees the fakes above.
    /// </summary>
    private static void ResetSeams()
    {
        InstallHooks.IsElevatedCheck = () => new WindowsFirewall().IsElevated;
        var firewallMethod = typeof(InstallHooks).GetMethod(
            "Firewall", BindingFlags.NonPublic | BindingFlags.Static)!;
        InstallHooks.FirewallFactory =
            (Func<WindowsFirewallLanAccess>)Delegate.CreateDelegate(
                typeof(Func<WindowsFirewallLanAccess>), firewallMethod);
    }
}
