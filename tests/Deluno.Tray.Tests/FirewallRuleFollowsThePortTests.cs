using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1372: after a port change from the tray, Deluno's firewall rule must allow the new port, not only the old one.</summary>
public sealed class FirewallRuleFollowsThePortTests
{
    [Fact]
    public async Task The_rule_moves_to_the_new_port_and_the_old_one_is_removed()
    {
        var fw = new PortFirewall(7879);
        var result = await FirewallPortMove.MoveAsync(fw, 7879, 7890, CancellationToken.None);

        Assert.Equal(FirewallPortMoveOutcome.Moved, result.Outcome);
        Assert.Equal(["7890"], fw.Ports);
        Assert.Equal(LanAccessStates.Open, (await new WindowsFirewallLanAccess(7890, fw).GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Nothing_is_added_for_a_person_who_never_allowed_other_devices()
    {
        var fw = new PortFirewall();
        var result = await FirewallPortMove.MoveAsync(fw, 7879, 7890, CancellationToken.None);

        Assert.Equal(FirewallPortMoveOutcome.NothingToMove, result.Outcome);
        Assert.Empty(fw.Calls);
    }

    [Fact]
    public async Task A_declined_permission_prompt_leaves_the_old_rule_and_says_so()
    {
        var fw = new PortFirewall(7879) { DeclineAdd = true };
        var result = await FirewallPortMove.MoveAsync(fw, 7879, 7890, CancellationToken.None);

        Assert.Equal(FirewallPortMoveOutcome.Declined, result.Outcome);
        Assert.Contains("7890", result.Message);
        Assert.Equal(["7879"], fw.Ports);
    }

    [Fact]
    public async Task The_same_port_changes_nothing()
    {
        var fw = new PortFirewall(7879);
        Assert.Equal(FirewallPortMoveOutcome.NothingToMove, (await FirewallPortMove.MoveAsync(fw, 7879, 7879, CancellationToken.None)).Outcome);
        Assert.Empty(fw.Calls);
    }

    private sealed class PortFirewall : IWindowsFirewall
    {
        private readonly List<FirewallRule> _rules = [];

        public PortFirewall(params int[] ports)
        {
            foreach (var p in ports) _rules.Add(Rule(p));
        }

        public bool DeclineAdd { get; init; }
        public List<string> Calls { get; } = [];
        public IEnumerable<string> Ports => _rules.Select(r => r.LocalPorts!);
        public bool IsElevated => true;
        public FirewallSnapshot? Read() => new(2, 0, _rules.ToList());

        private static FirewallRule Rule(int port) => new($"Deluno (port {port})", true, true, true, 6, port.ToString(), 7, null);

        public Task<FirewallCommandResult> RunNetshAsync(string args, bool elevate, CancellationToken ct)
        {
            Calls.Add(args);
            var port = int.Parse(Regex.Match(args, @"\(port (\d+)\)").Groups[1].Value);
            if (args.Contains("add rule"))
            {
                if (DeclineAdd) return Task.FromResult(new FirewallCommandResult(1223, "cancelled", Declined: true));
                _rules.Add(Rule(port));
                return Task.FromResult(new FirewallCommandResult(0, "Ok."));
            }

            _rules.RemoveAll(r => r.Name == $"Deluno (port {port})");
            return Task.FromResult(new FirewallCommandResult(0, "Deleted 1 rule(s)."));
        }
    }
}
