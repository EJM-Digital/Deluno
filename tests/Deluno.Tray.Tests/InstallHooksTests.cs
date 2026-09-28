using System.Reflection;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// Velopack kills the install/update hooks after 30 seconds. A cold,
/// non-elevated first run (every ordinary install) once took 23+ seconds to
/// log anything because it built a <see cref="WindowsFirewallLanAccess"/> --
/// touching <c>DelunoServer</c>'s statics and <c>AppSettings.Load()</c> --
/// before checking whether it even had the rights to use it (#968).
/// <see cref="InstallHooks.OnAfterInstall"/> must now return on the
/// non-elevated path before ever calling <see cref="InstallHooks.FirewallFactory"/>.
/// </summary>
public sealed class InstallHooksTests
{
    [Fact]
    public void OnAfterInstall_not_elevated_returns_without_building_the_firewall()
    {
        var firewallFactoryCalled = false;
        InstallHooks.IsElevatedCheck = () => false;
        InstallHooks.FirewallFactory = () =>
        {
            firewallFactoryCalled = true;
            throw new InvalidOperationException(
                "The fast, non-elevated path must never touch DelunoServer/AppSettings.");
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
    public void OnAfterInstall_elevated_runs_the_firewall_step()
    {
        var firewallFactoryCalled = false;
        InstallHooks.IsElevatedCheck = () => true;
        InstallHooks.FirewallFactory = () =>
        {
            firewallFactoryCalled = true;
            // A real WindowsFirewallLanAccess talks to Windows Firewall and
            // netsh; return one built against a fake so AllowAsync completes
            // without touching the machine.
            return new WindowsFirewallLanAccess(0, new FakeFirewall());
        };

        try
        {
            InstallHooks.OnAfterInstall();
        }
        finally
        {
            ResetSeams();
        }

        Assert.True(firewallFactoryCalled);
    }

    [Fact]
    public void OnAfterInstall_never_throws_when_the_firewall_step_fails()
    {
        InstallHooks.IsElevatedCheck = () => true;
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

    private sealed class FakeFirewall : IWindowsFirewall
    {
        public bool IsElevated => true;

        public FirewallSnapshot? Read() => new(CurrentProfiles: 2, FirewallOffFor: 0, Rules: []);

        public Task<FirewallCommandResult> RunNetshAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
            => Task.FromResult(new FirewallCommandResult(0, string.Empty));
    }
}
