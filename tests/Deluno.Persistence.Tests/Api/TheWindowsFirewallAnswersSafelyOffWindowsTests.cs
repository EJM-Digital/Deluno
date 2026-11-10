using Deluno.Api.Network;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The LAN-access rule is read and written through Windows Firewall, but the code is built and tested on Linux too. Off Windows it
/// must not pretend to be able to change the firewall: it is not elevated, it reads no rules, and a change is reported as not
/// started rather than thrown. On Windows the real calls are exercised by the Windows-only job; here only what is the same on both
/// (the port and rule name it was made for, and that a default firewall can be built) is checked, and nothing runs netsh.
/// </summary>
public sealed class TheWindowsFirewallAnswersSafelyOffWindowsTests
{
    [Fact]
    public void The_rule_follows_the_port_it_was_made_for_with_a_firewall_and_program_it_was_given_or_the_defaults()
    {
        var given = new WindowsFirewallLanAccess(7890, new WindowsFirewall(), "/opt/deluno/Deluno.Api");
        var defaults = new WindowsFirewallLanAccess(7891);

        Assert.Equal(7890, given.Port);
        Assert.Equal("Deluno (port 7890)", given.RuleName);
        Assert.Equal(7891, defaults.Port);
        Assert.Equal(WindowsFirewallLanAccess.RuleNameFor(7891), defaults.RuleName);
    }

    [Fact]
    public async Task Off_Windows_the_firewall_is_not_elevated_reads_no_rules_and_reports_a_change_as_not_started()
    {
        if (OperatingSystem.IsWindows())
        {
            // The real Windows calls (the COM read, netsh, the permission prompt) belong to the Windows-only job; running netsh
            // from a test would change or prompt on the developer's own firewall.
            return;
        }

        var firewall = new WindowsFirewall();

        Assert.False(firewall.IsElevated);
        Assert.Null(firewall.Read());
        var plain = await firewall.RunNetshAsync("advfirewall show allprofiles", elevate: false, CancellationToken.None);
        Assert.Equal(-1, plain.ExitCode);
        Assert.False(plain.Declined);
        var elevated = await firewall.RunNetshAsync("advfirewall show allprofiles", elevate: true, CancellationToken.None);
        Assert.Equal(-1, elevated.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(elevated.Output));
    }
}
