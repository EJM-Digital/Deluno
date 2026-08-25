using Deluno.Platform.Contracts;

namespace Deluno.Recovery.Policies;

/// <summary>What Deluno should do with a download client's copy right now.</summary>
public enum SharingAction
{
    /// <summary>Leave it alone permanently — the user manages the client themselves.</summary>
    Leave,

    /// <summary>The sharing rule is not met yet. Hold, and say why.</summary>
    Wait,

    /// <summary>Ask the client to remove the item and its data.</summary>
    Reclaim,

    /// <summary>The rule can no longer be met. Raise it as a decision rather than acting.</summary>
    Ask
}

/// <summary>
/// The outcome, with the sentence a user should be shown. The reason is written
/// for the dashboard, not for a log: "still sharing for 2 more days" rather than
/// "seedingMinutes 2880 &lt; forHours 4320".
/// </summary>
public sealed record SharingDecision(SharingAction Action, string Reason);

/// <summary>
/// Decides when a completed, imported download has finished its obligation to
/// the site it came from (#288).
///
/// Deliberately pure: it takes what the client reported and the rule that
/// applies, and returns a decision. It performs no removal itself, in keeping
/// with the rest of this module — retention and removal are carried out by a
/// service that can be audited, and never as a side effect of evaluating.
/// </summary>
public static class SharingPolicyEvaluator
{
    /// <summary>
    /// <paramref name="ratio"/> and <paramref name="seedingMinutes"/> are what the
    /// download client reports. Both are null for protocols with no sharing
    /// phase — usenet — where there is nothing to wait for.
    /// </summary>
    public static SharingDecision Evaluate(
        SharingPolicy policy,
        bool supportsSharing,
        double? ratio,
        int? seedingMinutes)
    {
        var mode = SharingPolicy.NormalizeMode(policy.Mode);

        if (mode == SharingPolicy.ModeLeaveAlone)
        {
            return new(SharingAction.Leave, "Deluno is not managing this download client.");
        }

        if (mode == SharingPolicy.ModeTidyNow)
        {
            return new(SharingAction.Reclaim, "Tidied up straight away, because that is how this source is configured.");
        }

        // Usenet and anything else without a sharing phase has no obligation to
        // discharge, so waiting would hold the drive for a rule that can never
        // be met.
        if (!supportsSharing)
        {
            return new(SharingAction.Reclaim, "Tidied up straight away, because this download client does not share completed files.");
        }

        var hasTimeRule = policy.ForHours is > 0;
        var hasRatioRule = policy.UntilRatio is > 0;

        if (!hasTimeRule && !hasRatioRule)
        {
            return new(SharingAction.Reclaim, "Tidied up straight away, because no sharing target is set for this source.");
        }

        var sharedMinutes = seedingMinutes ?? 0;
        var timeMet = !hasTimeRule || sharedMinutes >= policy.ForHours!.Value * 60;
        var ratioMet = !hasRatioRule || (ratio ?? 0) >= policy.UntilRatio!.Value;

        // Both stated targets must be met. Asking for "14 days and ratio 1.0"
        // and reclaiming at whichever lands first would break the stricter half
        // of the rule, which is the half that gets accounts banned.
        if (timeMet && ratioMet)
        {
            return new(SharingAction.Reclaim, $"Finished sharing{DescribeMet(policy, ratio, sharedMinutes)}.");
        }

        var stuckAfterMinutes = Math.Max(1, policy.StuckAfterDays) * 24 * 60;
        if (sharedMinutes >= stuckAfterMinutes)
        {
            var shortfall = DescribeShortfall(policy, ratio, sharedMinutes);
            return SharingPolicy.NormalizeStuckAction(policy.StuckAction) switch
            {
                SharingPolicy.StuckKeepWaiting => new(
                    SharingAction.Wait,
                    $"Still sharing after {policy.StuckAfterDays} days and {shortfall}. Deluno is set to keep waiting."),
                SharingPolicy.StuckAsk => new(
                    SharingAction.Ask,
                    $"Shared for {policy.StuckAfterDays} days and {shortfall}. Deluno is waiting for you to decide."),
                _ => new(
                    SharingAction.Reclaim,
                    $"Gave up after {policy.StuckAfterDays} days: {shortfall}.")
            };
        }

        return new(SharingAction.Wait, DescribeRemaining(policy, ratio, sharedMinutes, timeMet, ratioMet));
    }

    private static string DescribeMet(SharingPolicy policy, double? ratio, int sharedMinutes)
    {
        var parts = new List<string>();
        if (policy.ForHours is > 0) parts.Add(FormatDuration(sharedMinutes));
        if (policy.UntilRatio is > 0) parts.Add($"ratio {ratio ?? 0:0.00}");
        return parts.Count == 0 ? string.Empty : $" — {string.Join(", ", parts)}";
    }

    private static string DescribeShortfall(SharingPolicy policy, double? ratio, int sharedMinutes)
    {
        if (policy.UntilRatio is > 0 && (ratio ?? 0) < policy.UntilRatio.Value)
        {
            return $"ratio reached {ratio ?? 0:0.00} of the {policy.UntilRatio.Value:0.00} this source asks for";
        }

        return $"shared for {FormatDuration(sharedMinutes)}, short of the target";
    }

    private static string DescribeRemaining(SharingPolicy policy, double? ratio, int sharedMinutes, bool timeMet, bool ratioMet)
    {
        var parts = new List<string>();

        if (!timeMet && policy.ForHours is > 0)
        {
            parts.Add($"{FormatDuration(policy.ForHours.Value * 60 - sharedMinutes)} left");
        }

        if (!ratioMet && policy.UntilRatio is > 0)
        {
            parts.Add($"ratio {ratio ?? 0:0.00} of {policy.UntilRatio.Value:0.00}");
        }

        return parts.Count == 0
            ? "Still sharing."
            : $"Still sharing — {string.Join(", ", parts)}.";
    }

    /// <summary>Whole units, in the words a person uses: "2 days", "4 hours", "20 minutes".</summary>
    private static string FormatDuration(int minutes)
    {
        var safe = Math.Max(0, minutes);
        if (safe >= 2880) return $"{safe / 1440} days";
        if (safe >= 1440) return "1 day";
        if (safe >= 120) return $"{safe / 60} hours";
        if (safe >= 60) return "1 hour";
        return $"{safe} minutes";
    }
}
