using Deluno.Contracts;
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

    /// <summary>
    /// Ask the client to stop seeding, but leave the entry and its files in
    /// place (#821) — the alternative to <see cref="Reclaim"/> when the
    /// sharing target is met, for a site where the user would rather keep the
    /// data around than have the client forget it.
    /// </summary>
    Stop,

    /// <summary>The rule can no longer be met. Raise it as a decision rather than acting.</summary>
    Ask
}

/// <summary>
/// The outcome, with the sentence a user should be shown. The reason is written
/// for the dashboard, not for a log: "still sharing for 2 more days" rather than
/// "seedingMinutes 2880 &lt; forHours 4320".
/// </summary>
/// <param name="Reason">
/// The whole sentence, standing on its own. This is what goes in the activity
/// feed, where nothing around it has already said what happened.
/// </param>
/// <param name="Detail">
/// The same fact with the part the surrounding context already supplies taken
/// out — "2 days left" rather than "Still sharing — 2 days left." A dashboard
/// section headed "Finished, still sharing" that then says "still sharing" on
/// every row is stating the same thing twice, which is a defect and not a
/// flourish. Falls back to the whole sentence where there is no shorter form.
/// </param>
public sealed record SharingDecision(SharingAction Action, string Reason, string? Detail = null)
{
    public string DetailOrReason => string.IsNullOrWhiteSpace(Detail) ? Reason : Detail;
}

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
    /// phase — usenet — where there is nothing to wait for. <paramref name="isPrivate"/>
    /// is the torrent's own reported tracker privacy (#815; null when unknown).
    /// </summary>
    public static SharingDecision Evaluate(
        SharingPolicy policy,
        bool supportsSharing,
        double? ratio,
        int? seedingMinutes,
        bool? isPrivate = null,
        SharingGuards? guards = null)
    {
        var mode = SharingPolicy.NormalizeMode(policy.Mode);

        if (mode == SharingPolicy.ModeLeaveAlone)
        {
            return new(SharingAction.Leave, "Deluno is not managing this download client.");
        }

        // #815: a policy scoped to public-only or private-only never reclaims
        // a download outside its scope - left alone the same way "leave
        // alone" mode is, so a private-tracker account is never put at risk
        // by a rule that was never meant to cover it.
        if (!policy.AppliesToPrivacy(isPrivate))
        {
            return new(SharingAction.Leave, "This policy does not cover this download's tracker privacy.");
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

        // Whether reaching the target removes the item or just stops seeding it
        // (#821). Either way the obligation is discharged; the only question is
        // whether the client is asked to forget the torrent or just pause it.
        var completesWithStop = SharingPolicy.NormalizeCompletionAction(policy.CompletionAction) == SharingPolicy.CompletionStop;
        var completionVerb = completesWithStop ? "Stopped seeding" : "Finished sharing";
        var completionAction = completesWithStop ? SharingAction.Stop : SharingAction.Reclaim;

        // Both stated targets must be met. Asking for "14 days and ratio 1.0"
        // and reclaiming at whichever lands first would break the stricter half
        // of the rule, which is the half that gets accounts banned.
        if (timeMet && ratioMet)
        {
            return CheckGuards(guards) ?? new(completionAction, $"{completionVerb}{DescribeMet(policy, ratio, sharedMinutes)}.");
        }

        var stuckAfterMinutes = Math.Max(1, policy.StuckAfterDays) * 24 * 60;
        if (sharedMinutes >= stuckAfterMinutes)
        {
            var shortfall = DescribeShortfall(policy, ratio, sharedMinutes);
            return SharingPolicy.NormalizeStuckAction(policy.StuckAction) switch
            {
                SharingPolicy.StuckKeepWaiting => new(
                    SharingAction.Wait,
                    $"Still sharing after {PluralWords.Count(policy.StuckAfterDays, "day")} and {shortfall}. Deluno is set to keep waiting.",
                    $"{PluralWords.Count(policy.StuckAfterDays, "day")} in, {shortfall} — set to keep waiting"),
                SharingPolicy.StuckAsk => new(
                    SharingAction.Ask,
                    $"Shared for {PluralWords.Count(policy.StuckAfterDays, "day")} and {shortfall}. Deluno is waiting for you to decide.",
                    $"{PluralWords.Count(policy.StuckAfterDays, "day")} in, {shortfall} — waiting for you to decide"),
                _ => CheckGuards(guards) ?? new(
                    completionAction,
                    $"Gave up after {PluralWords.Count(policy.StuckAfterDays, "day")}: {shortfall}.")
            };
        }

        var remaining = DescribeRemaining(policy, ratio, sharedMinutes, timeMet, ratioMet);
        return new(SharingAction.Wait, $"Still sharing{(remaining is null ? string.Empty : $" — {remaining}")}.", remaining);
    }

    /// <summary>
    /// The rule matrix's two guards (#820), from a matched
    /// <c>SharingRuleItem</c> — kept out of <see cref="Deluno.Platform.Contracts.SharingPolicy"/>
    /// because they hold a decision at Wait rather than change what the
    /// decision would otherwise be, which is a different kind of input to the
    /// same evaluation.
    /// </summary>
    /// <param name="MinSeeders">Hold at Wait while fewer than this many peers are seeding it.</param>
    /// <param name="CurrentSeeders">What the client currently reports.</param>
    /// <param name="MaxInactiveDays">Hold at Wait once it has gone this long with no speed at all.</param>
    /// <param name="InactiveDays">How many days it has already gone with no speed.</param>
    public sealed record SharingGuards(int? MinSeeders, int? CurrentSeeders, int? MaxInactiveDays, int? InactiveDays);

    /// <summary>
    /// Null when neither guard applies or both are satisfied, meaning the
    /// caller's own Reclaim/Stop/give-up decision stands.
    /// </summary>
    private static SharingDecision? CheckGuards(SharingGuards? guards)
    {
        if (guards is null)
        {
            return null;
        }

        if (guards.MinSeeders is > 0 && (guards.CurrentSeeders ?? 0) < guards.MinSeeders.Value)
        {
            var seeders = guards.CurrentSeeders ?? 0;
            return new(
                SharingAction.Wait,
                $"Still sharing — only {PluralWords.Count(seeders, "seeder")}, below the {guards.MinSeeders} this rule holds out for.",
                $"only {seeders} of {guards.MinSeeders} seeders");
        }

        if (guards.MaxInactiveDays is > 0 && guards.InactiveDays is >= 0 && guards.InactiveDays >= guards.MaxInactiveDays)
        {
            return new(
                SharingAction.Wait,
                $"Still sharing — inactive for {PluralWords.Count(guards.InactiveDays.Value, "day")}, and this rule holds an inactive download rather than removing it.",
                $"inactive {PluralWords.Count(guards.InactiveDays.Value, "day")}");
        }

        return null;
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

    /// <summary>
    /// What is left to do, or null when the rule cannot say — the caller wraps
    /// it into a sentence and also keeps it on its own for surfaces that have
    /// already said "still sharing" in their heading.
    /// </summary>
    private static string? DescribeRemaining(SharingPolicy policy, double? ratio, int sharedMinutes, bool timeMet, bool ratioMet)
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

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Whole units, in the words a person uses: "2 days", "4 hours", "20 minutes".</summary>
    private static string FormatDuration(int minutes)
    {
        var safe = Math.Max(0, minutes);
        if (safe >= 2880) return $"{safe / 1440} days";
        if (safe >= 1440) return "1 day";
        if (safe >= 120) return $"{safe / 60} hours";
        if (safe >= 60) return "1 hour";
        return PluralWords.Count(safe, "minute");
    }
}
