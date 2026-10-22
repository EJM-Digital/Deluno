using Deluno.Api.Network;

namespace Deluno.Tray;

public enum FirewallPortMoveOutcome
{
    /// <summary>Deluno has no rule of its own on the old port (the person never allowed other devices, or another rule does), so there is nothing to move.</summary>
    NothingToMove,
    /// <summary>Deluno's rule now allows the new port and the old one is gone.</summary>
    Moved,
    /// <summary>Windows asked for permission to change the firewall and it was not given: the old rule is left as it was.</summary>
    Declined,
    /// <summary>Windows did not add the rule for the new port: the old rule is left as it was.</summary>
    Failed
}

public sealed record FirewallPortMoveResult(FirewallPortMoveOutcome Outcome, string? Message);

/// <summary>
/// After a port change, moves Deluno's own firewall rule to the new port so other devices still reach Deluno with no
/// extra step (#1372). The rule is per port, so the new rule is added first and the old one is removed only once the new one
/// holds; nothing is changed for a person who never allowed other devices.
/// </summary>
public static class FirewallPortMove
{
    public static async Task<FirewallPortMoveResult> MoveAsync(IWindowsFirewall firewall, int oldPort, int newPort, CancellationToken cancellationToken)
    {
        var old = new WindowsFirewallLanAccess(oldPort, firewall);
        if (oldPort == newPort || !old.HasOwnRule())
        {
            return new(FirewallPortMoveOutcome.NothingToMove, null);
        }

        var added = await new WindowsFirewallLanAccess(newPort, firewall).AllowAsync(cancellationToken).ConfigureAwait(false);
        if (added.State != LanAccessStates.Open)
        {
            var declined = added.Message.Contains("not given", StringComparison.OrdinalIgnoreCase);
            return new(
                declined ? FirewallPortMoveOutcome.Declined : FirewallPortMoveOutcome.Failed,
                $"Other devices cannot reach Deluno on port {newPort} yet. {added.Message} Choose Allow other devices on your network to try again.");
        }

        await old.RemoveAsync(cancellationToken).ConfigureAwait(false);
        return new(FirewallPortMoveOutcome.Moved, $"Other devices on your network can reach Deluno on port {newPort}.");
    }
}
