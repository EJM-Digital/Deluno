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
