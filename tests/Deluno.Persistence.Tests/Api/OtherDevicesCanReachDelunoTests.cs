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
/// LAN just could not connect. The first version of the fix then made the
/// opposite mistake on the rig: it looked only for its own rule by name, and
/// told the owner other devices were turned away while he was using Deluno from
/// one, through an older rule for the same port.</para>
/// </summary>
public sealed class OtherDevicesCanReachDelunoTests
{
    private const int Public = 4;
    private const int Private = 2;
    private const int AllProfiles = 0x7FFFFFFF;
    private const string Host = @"C:\Deluno\App\Deluno.Host.exe";

    [Fact]
    public async Task No_rule_for_the_port_is_reported_as_blocked_with_a_way_to_allow_it()
    {
        var status = await Access(new FakeFirewall(Public)).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Blocked, status.State);
        Assert.True(status.CanAllow);
        Assert.Contains("5099", status.Message);
    }

    [Fact]
    public async Task Any_rule_that_opens_the_port_counts_not_only_Delunos_own()
    {
        var firewall = new FakeFirewall(Public, Rule("Deluno E2E lab - Deluno Host", ports: "5099", profiles: AllProfiles));

        var status = await Access(firewall).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
        Assert.Equal("Deluno E2E lab - Deluno Host", status.RuleName);
        Assert.False(status.CanAllow);
    }

    [Fact]
    public async Task A_firewall_switched_off_for_this_network_lets_everything_in()
    {
        var status = await Access(new FakeFirewall(Public, firewallOffFor: Public)).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
    }

    [Theory]
    [InlineData("5099", AllProfiles, null, true, true, true)]
    [InlineData("5000-6000", AllProfiles, null, true, true, true)]
    [InlineData("80,443,5099", AllProfiles, null, true, true, true)]
    [InlineData("*", AllProfiles, null, true, true, true)]
    [InlineData("5098", AllProfiles, null, true, true, false)]
    [InlineData("5099", Private, null, true, true, false)]
    [InlineData("5099", AllProfiles, @"C:\Other\app.exe", true, true, false)]
    [InlineData("5099", AllProfiles, Host, true, true, true)]
    [InlineData("5099", AllProfiles, null, false, true, false)]
    [InlineData("5099", AllProfiles, null, true, false, false)]
    public async Task A_rule_opens_the_port_only_for_this_port_this_network_and_this_program(
        string ports, int profiles, string? program, bool enabled, bool allow, bool open)
    {
        var firewall = new FakeFirewall(Public, Rule("Some rule", ports, profiles, program, enabled, allow));

        var status = await Access(firewall).GetStatusAsync(CancellationToken.None);

        Assert.Equal(open ? LanAccessStates.Open : LanAccessStates.Blocked, status.State);
    }

    [Fact]
    public async Task A_firewall_Windows_will_not_describe_is_not_guessed_at()
    {
        var status = await Access(new FakeFirewall(Public) { Unreadable = true }).GetStatusAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.NotChecked, status.State);
        Assert.False(status.CanAllow);
    }

    [Fact]
    public async Task Allowing_without_administrator_rights_asks_Windows_and_covers_every_network_profile()
    {
        var firewall = new FakeFirewall(Public);

        var status = await Access(firewall).AllowAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Open, status.State);
        var add = Assert.Single(firewall.Calls, call => call.Arguments.Contains("add rule"));
        Assert.True(add.Elevate);
        Assert.Contains("name=\"Deluno (port 5099)\"", add.Arguments);
        Assert.Contains("localport=5099", add.Arguments);
        Assert.Contains("profile=any", add.Arguments);
        Assert.Contains("dir=in action=allow protocol=TCP", add.Arguments);
    }

    [Fact]
    public async Task Allowing_as_an_administrator_adds_the_rule_without_a_prompt()
    {
        var firewall = new FakeFirewall(Public) { Elevated = true };

        await Access(firewall).AllowAsync(CancellationToken.None);

        Assert.False(Assert.Single(firewall.Calls, call => call.Arguments.Contains("add rule")).Elevate);
    }

    [Fact]
    public async Task A_declined_permission_prompt_leaves_it_blocked_and_says_why()
    {
        var status = await Access(new FakeFirewall(Public) { DeclineAdd = true }).AllowAsync(CancellationToken.None);

        Assert.Equal(LanAccessStates.Blocked, status.State);
        Assert.True(status.CanAllow);
        Assert.Contains("permission", status.Message);
    }

    [Fact]
    public async Task Allowing_when_the_port_is_already_open_adds_nothing()
    {
        var firewall = new FakeFirewall(Public, Rule("Deluno E2E lab - Deluno Host", "5099", AllProfiles));

        await Access(firewall).AllowAsync(CancellationToken.None);

        Assert.Empty(firewall.Calls);
    }

    [Fact]
    public async Task Uninstalling_removes_only_Delunos_own_rule()
    {
        var withOwn = new FakeFirewall(Public, Rule("Deluno (port 5099)", "5099", AllProfiles));
        var withOther = new FakeFirewall(Public, Rule("Deluno E2E lab - Deluno Host", "5099", AllProfiles));

        Assert.True(Access(withOwn).HasOwnRule());
        Assert.False(Access(withOther).HasOwnRule());
        await Access(withOwn).RemoveAsync(CancellationToken.None);
        Assert.Contains(withOwn.Calls, call => call.Arguments.Contains("delete rule name=\"Deluno (port 5099)\""));
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

    private static WindowsFirewallLanAccess Access(FakeFirewall firewall) => new(5099, firewall, Host);

    private static FirewallRule Rule(string name, string ports, int profiles, string? program = null, bool enabled = true, bool allow = true)
        => new(name, enabled, Inbound: true, allow, Protocol: 6, ports, profiles, program);

    private sealed class FakeFirewall(int currentProfiles, params FirewallRule[] rules) : IWindowsFirewall
    {
        private readonly List<FirewallRule> _rules = [.. rules];

        public int FirewallOffFor { get; init; }

        public bool Elevated { get; init; }

        public bool DeclineAdd { get; init; }

        public bool Unreadable { get; init; }

        public List<(string Arguments, bool Elevate)> Calls { get; } = [];

        public bool IsElevated => Elevated;

        public FakeFirewall(int currentProfiles, int firewallOffFor) : this(currentProfiles)
            => FirewallOffFor = firewallOffFor;

        public FirewallSnapshot? Read() => Unreadable ? null : new FirewallSnapshot(currentProfiles, FirewallOffFor, _rules);

        public Task<FirewallCommandResult> RunNetshAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
        {
            Calls.Add((netshArguments, elevate));
            if (netshArguments.Contains("add rule"))
            {
                if (DeclineAdd)
                {
                    return Task.FromResult(new FirewallCommandResult(1223, "The operation was canceled by the user.", Declined: true));
                }

                _rules.Add(Rule("Deluno (port 5099)", "5099", AllProfiles));
                return Task.FromResult(new FirewallCommandResult(0, "Ok."));
            }

            _rules.RemoveAll(rule => rule.Name == "Deluno (port 5099)");
            return Task.FromResult(new FirewallCommandResult(0, "Deleted 1 rule(s)."));
        }
    }
}
