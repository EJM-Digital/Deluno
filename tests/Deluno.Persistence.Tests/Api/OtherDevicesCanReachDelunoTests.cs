using System.Net.Http.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Whether other devices on the network can reach Deluno, and the one step
/// that lets them (#623).
///
/// <para><b>Why this file exists.</b> The installer added the Windows Firewall
/// rule from a callback it thought ran as an administrator. It does not, so the
/// rule was never there. The failure went to a log file, and another PC on the
/// LAN just could not connect.</para>
/// </summary>
public sealed class OtherDevicesCanReachDelunoTests
{
    [Fact]
    public async Task No_rule_for_the_port_is_reported_as_blocked_with_a_way_to_allow_it()
    {
        var netsh = new FakeNetsh(ruleExists: false);

        var status = await new WindowsFirewallLanAccess(5099, netsh).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Blocked, status.State);
        Assert.True(status.CanAllow);
        Assert.Contains("5099", status.Message);
        Assert.Contains("show rule name=\"Deluno (port 5099)\"", netsh.Commands.Single());
    }

    [Fact]
    public async Task A_rule_for_the_port_is_reported_as_open()
    {
        var status = await new WindowsFirewallLanAccess(5099, new FakeNetsh(ruleExists: true)).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
        Assert.False(status.CanAllow);
    }

    [Fact]
    public async Task Allowing_without_administrator_rights_asks_Windows_and_covers_every_network_profile()
    {
        var netsh = new FakeNetsh(ruleExists: false, elevated: false);

        var status = await new WindowsFirewallLanAccess(5099, netsh).AllowAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
        var add = Assert.Single(netsh.Calls, call => call.Arguments.Contains("add rule"));
        Assert.True(add.Elevate);
        Assert.Contains("localport=5099", add.Arguments);
        Assert.Contains("profile=any", add.Arguments);
        Assert.Contains("dir=in action=allow protocol=TCP", add.Arguments);
    }

    [Fact]
    public async Task Allowing_as_an_administrator_adds_the_rule_without_a_prompt()
    {
        var netsh = new FakeNetsh(ruleExists: false, elevated: true);

        await new WindowsFirewallLanAccess(5099, netsh).AllowAsync(CancellationToken.None);

        Assert.False(Assert.Single(netsh.Calls, call => call.Arguments.Contains("add rule")).Elevate);
    }

    [Fact]
    public async Task A_declined_permission_prompt_leaves_it_blocked_and_says_why()
    {
        var netsh = new FakeNetsh(ruleExists: false, declineAdd: true);

        var status = await new WindowsFirewallLanAccess(5099, netsh).AllowAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Blocked, status.State);
        Assert.True(status.CanAllow);
        Assert.Contains("permission", status.Message);
    }

    [Fact]
    public async Task Allowing_when_the_rule_is_already_there_changes_nothing()
    {
        var netsh = new FakeNetsh(ruleExists: true);

        var status = await new WindowsFirewallLanAccess(5099, netsh).AllowAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
        Assert.DoesNotContain(netsh.Calls, call => call.Arguments.Contains("add rule"));
    }

    [Fact]
    public async Task Uninstalling_removes_the_rule_Deluno_added()
    {
        var netsh = new FakeNetsh(ruleExists: true);

        await new WindowsFirewallLanAccess(5099, netsh).RemoveAsync(CancellationToken.None);

        Assert.Contains(netsh.Calls, call => call.Arguments.Contains("delete rule name=\"Deluno (port 5099)\""));
    }

    [Fact]
    public async Task A_host_with_no_firewall_to_look_after_says_so_and_offers_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<LanAccessStatus>("/api/system/lan-access");
        var allowed = await app.Client.PostAsync("/api/system/lan-access/allow", null);

        Assert.Equal(LanAccessStates.NotChecked, status?.State);
        Assert.False(status?.CanAllow);
        Assert.True(allowed.IsSuccessStatusCode);
    }

    private sealed class FakeNetsh(bool ruleExists, bool elevated = false, bool declineAdd = false) : IFirewallCommandRunner
    {
        private bool _ruleExists = ruleExists;

        public List<(string Arguments, bool Elevate)> Calls { get; } = [];

        public IEnumerable<string> Commands => Calls.Select(call => call.Arguments);

        public bool IsElevated => elevated;

        public Task<FirewallCommandResult> RunAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
        {
            Calls.Add((netshArguments, elevate));
            if (netshArguments.Contains("show rule"))
            {
                return Task.FromResult(new FirewallCommandResult(_ruleExists ? 0 : 1, _ruleExists ? "Rule Name: Deluno" : "No rules match the specified criteria."));
            }

            if (netshArguments.Contains("add rule"))
            {
                if (declineAdd)
                {
                    return Task.FromResult(new FirewallCommandResult(1223, "The operation was canceled by the user.", Declined: true));
                }

                _ruleExists = true;
                return Task.FromResult(new FirewallCommandResult(0, "Ok."));
            }

            _ruleExists = false;
            return Task.FromResult(new FirewallCommandResult(0, "Deleted 1 rule(s)."));
        }
    }
}
