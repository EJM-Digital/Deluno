using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The rules that decide when a download client's copy has finished its
/// obligation to the site it came from (#288).
/// </summary>
public sealed class SharingPolicyEvaluatorTests
{
    private static SharingPolicy Policy(
        string mode = SharingPolicy.ModeShareThenTidy,
        int? forHours = 72,
        double? untilRatio = null,
        string stuck = SharingPolicy.StuckGiveUp,
        int stuckAfterDays = 14,
        string completionAction = SharingPolicy.CompletionReclaim,
        string privacyScope = "both")
        => new(mode, forHours, untilRatio, stuck, stuckAfterDays, completionAction, privacyScope);

    private static SharingDecision Evaluate(SharingPolicy policy, double? ratio = null, int? seedingMinutes = 0, bool supportsSharing = true, bool? isPrivate = null)
        => SharingPolicyEvaluator.Evaluate(policy, supportsSharing, ratio, seedingMinutes, isPrivate);

    // ── Modes ─────────────────────────────────────────────────────────────

    [Fact]
    public void Leave_alone_never_touches_the_client()
    {
        var decision = Evaluate(Policy(mode: SharingPolicy.ModeLeaveAlone), seedingMinutes: 0);

        Assert.Equal(SharingAction.Leave, decision.Action);
    }

    /// <summary>#815: a private-only policy never reclaims a public download - left alone, the same as "leave alone" mode.</summary>
    [Fact]
    public void A_policy_scoped_to_private_only_leaves_a_public_download_alone()
    {
        var decision = Evaluate(Policy(mode: SharingPolicy.ModeTidyNow, privacyScope: "private"), isPrivate: false);

        Assert.Equal(SharingAction.Leave, decision.Action);
    }

    /// <summary>#815: a public-only policy never reclaims a private download.</summary>
    [Fact]
    public void A_policy_scoped_to_public_only_leaves_a_private_download_alone()
    {
        var decision = Evaluate(Policy(mode: SharingPolicy.ModeTidyNow, privacyScope: "public"), isPrivate: true);

        Assert.Equal(SharingAction.Leave, decision.Action);
    }

    /// <summary>The flip side: a policy matching the download's actual privacy still reclaims it.</summary>
    [Fact]
    public void A_policy_scoped_to_the_downloads_own_privacy_still_reclaims_it()
    {
        var decision = Evaluate(Policy(mode: SharingPolicy.ModeTidyNow, privacyScope: "private"), isPrivate: true);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
    }

    [Fact]
    public void Tidy_now_reclaims_immediately_even_with_a_target_set()
    {
        var decision = Evaluate(Policy(mode: SharingPolicy.ModeTidyNow, forHours: 72, untilRatio: 2.0), seedingMinutes: 0);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
    }

    [Fact]
    public void A_client_that_does_not_share_is_reclaimed_at_once()
    {
        // Usenet has no sharing phase, so waiting would hold the drive for a
        // rule that can never be satisfied.
        var decision = Evaluate(Policy(forHours: 72), supportsSharing: false);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
        Assert.Contains("does not share", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Time and ratio, alone and together ────────────────────────────────

    [Fact]
    public void Waits_until_a_time_target_is_reached()
    {
        Assert.Equal(SharingAction.Wait, Evaluate(Policy(forHours: 72), seedingMinutes: 60).Action);
        Assert.Equal(SharingAction.Reclaim, Evaluate(Policy(forHours: 72), seedingMinutes: 72 * 60).Action);
    }

    [Fact]
    public void Waits_until_a_ratio_target_is_reached()
    {
        var policy = Policy(forHours: null, untilRatio: 1.0);

        Assert.Equal(SharingAction.Wait, Evaluate(policy, ratio: 0.4, seedingMinutes: 30).Action);
        Assert.Equal(SharingAction.Reclaim, Evaluate(policy, ratio: 1.0, seedingMinutes: 30).Action);
    }

    [Fact]
    public void When_both_targets_are_set_both_have_to_be_met()
    {
        // Reclaiming at whichever lands first would break the stricter half of
        // the rule, and the stricter half is the half that gets accounts banned.
        var policy = Policy(forHours: 72, untilRatio: 1.0);

        Assert.Equal(SharingAction.Wait, Evaluate(policy, ratio: 1.5, seedingMinutes: 60).Action);
        Assert.Equal(SharingAction.Wait, Evaluate(policy, ratio: 0.2, seedingMinutes: 72 * 60).Action);
        Assert.Equal(SharingAction.Reclaim, Evaluate(policy, ratio: 1.0, seedingMinutes: 72 * 60).Action);
    }

    [Fact]
    public void A_rule_with_no_target_at_all_reclaims_rather_than_waiting_forever()
    {
        var decision = Evaluate(Policy(forHours: null, untilRatio: null), seedingMinutes: 10);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
    }

    // ── When the target can never be reached ──────────────────────────────

    [Fact]
    public void Gives_up_at_the_cap_and_says_what_was_missed()
    {
        // A torrent with no peers never climbs; holding the drive forever is
        // the failure this whole feature exists to prevent.
        var decision = Evaluate(
            Policy(forHours: null, untilRatio: 2.0, stuck: SharingPolicy.StuckGiveUp, stuckAfterDays: 14),
            ratio: 0.3,
            seedingMinutes: 14 * 24 * 60);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
        Assert.Contains("Gave up after 14 days", decision.Reason);
        Assert.Contains("0.30", decision.Reason);
        Assert.Contains("2.00", decision.Reason);
    }

    [Fact]
    public void Keeps_waiting_when_configured_to()
    {
        var decision = Evaluate(
            Policy(forHours: null, untilRatio: 2.0, stuck: SharingPolicy.StuckKeepWaiting),
            ratio: 0.3,
            seedingMinutes: 20 * 24 * 60);

        Assert.Equal(SharingAction.Wait, decision.Action);
        Assert.Contains("keep waiting", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Asks_when_configured_to()
    {
        var decision = Evaluate(
            Policy(forHours: null, untilRatio: 2.0, stuck: SharingPolicy.StuckAsk),
            ratio: 0.3,
            seedingMinutes: 20 * 24 * 60);

        Assert.Equal(SharingAction.Ask, decision.Action);
    }

    [Fact]
    public void The_cap_does_not_fire_while_the_target_is_still_being_met()
    {
        // Reaching the target on the same tick as the cap must reclaim as a
        // success, not report it as having been given up on.
        var decision = Evaluate(
            Policy(forHours: null, untilRatio: 1.0, stuckAfterDays: 14),
            ratio: 1.0,
            seedingMinutes: 14 * 24 * 60);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
        Assert.DoesNotContain("Gave up", decision.Reason);
    }

    // ── The sentence a user reads ─────────────────────────────────────────

    [Fact]
    public void Waiting_says_how_much_longer_in_plain_words()
    {
        var decision = Evaluate(Policy(forHours: 72), seedingMinutes: 24 * 60);

        Assert.Equal(SharingAction.Wait, decision.Action);
        Assert.Contains("2 days left", decision.Reason);
    }

    [Fact]
    public void Waiting_on_a_ratio_shows_progress_towards_it()
    {
        var decision = Evaluate(Policy(forHours: null, untilRatio: 1.0), ratio: 0.42, seedingMinutes: 30);

        Assert.Contains("0.42", decision.Reason);
        Assert.Contains("1.00", decision.Reason);
    }

    /// <summary>
    /// The dashboard heads this list "Finished, still sharing", so a row that
    /// then reads "Still sharing — 2 days left" states the same thing twice.
    /// The detail is the same fact with that half removed; the whole sentence
    /// stays available for the activity feed, where nothing has said it yet.
    /// </summary>
    [Fact]
    public void The_detail_leaves_out_what_its_heading_already_says()
    {
        var decision = Evaluate(Policy(forHours: 72), seedingMinutes: 24 * 60);

        Assert.Equal("2 days left", decision.Detail);
        Assert.Equal("Still sharing — 2 days left.", decision.Reason);
        Assert.DoesNotContain("still sharing", decision.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waiting_for_a_decision_says_so_in_the_detail()
    {
        var decision = Evaluate(
            Policy(forHours: null, untilRatio: 2.0, stuck: SharingPolicy.StuckAsk, stuckAfterDays: 14),
            ratio: 0.30,
            seedingMinutes: 15 * 24 * 60);

        Assert.Equal(SharingAction.Ask, decision.Action);
        Assert.Contains("waiting for you to decide", decision.DetailOrReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Inheritance ───────────────────────────────────────────────────────

    [Fact]
    public void A_source_override_only_has_to_state_what_differs()
    {
        var global = new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14, SharingPolicy.CompletionStop);
        var siteWantsARatio = new SharingPolicy(string.Empty, null, 2.0, string.Empty, 0, string.Empty);

        var effective = siteWantsARatio.InheritFrom(global);

        Assert.Equal(SharingPolicy.ModeShareThenTidy, effective.Mode);
        Assert.Equal(72, effective.ForHours);
        Assert.Equal(2.0, effective.UntilRatio);
        Assert.Equal(SharingPolicy.StuckGiveUp, effective.StuckAction);
        Assert.Equal(14, effective.StuckAfterDays);
        Assert.Equal(SharingPolicy.CompletionStop, effective.CompletionAction);
    }

    [Fact]
    public void The_shipped_default_is_safe_for_someone_who_reads_nothing()
    {
        // Keeps sharing long enough that no site is upset, then reclaims on its
        // own, and gives up rather than holding the drive indefinitely.
        Assert.Equal(SharingPolicy.ModeShareThenTidy, SharingPolicy.Default.Mode);
        Assert.Equal(72, SharingPolicy.Default.ForHours);
        Assert.Equal(SharingPolicy.StuckGiveUp, SharingPolicy.Default.StuckAction);
        Assert.Equal(14, SharingPolicy.Default.StuckAfterDays);
        Assert.Equal(SharingPolicy.CompletionReclaim, SharingPolicy.Default.CompletionAction);
    }

    // ── Stop seeding without deleting (#821) ──────────────────────────────

    [Fact]
    public void Reaching_the_target_reclaims_by_default()
    {
        var decision = Evaluate(Policy(forHours: 72), seedingMinutes: 72 * 60);

        Assert.Equal(SharingAction.Reclaim, decision.Action);
        Assert.Contains("Finished sharing", decision.Reason);
    }

    [Fact]
    public void Reaching_the_target_stops_seeding_instead_when_configured()
    {
        var decision = Evaluate(Policy(forHours: 72, completionAction: SharingPolicy.CompletionStop), seedingMinutes: 72 * 60);

        Assert.Equal(SharingAction.Stop, decision.Action);
        Assert.Contains("Stopped seeding", decision.Reason);
    }

    [Fact]
    public void Giving_up_stops_seeding_instead_of_reclaiming_when_configured()
    {
        var decision = Evaluate(
            Policy(forHours: 72, untilRatio: 5.0, stuck: SharingPolicy.StuckGiveUp, stuckAfterDays: 14, completionAction: SharingPolicy.CompletionStop),
            ratio: 0.1,
            seedingMinutes: 14 * 24 * 60);

        Assert.Equal(SharingAction.Stop, decision.Action);
    }

    [Fact]
    public void Tidy_now_and_no_sharing_phase_always_reclaim_regardless_of_the_completion_action()
    {
        // There is nothing to "stop" seeding on a download that was never
        // going to seed in the first place.
        Assert.Equal(
            SharingAction.Reclaim,
            Evaluate(Policy(mode: SharingPolicy.ModeTidyNow, completionAction: SharingPolicy.CompletionStop)).Action);
        Assert.Equal(
            SharingAction.Reclaim,
            Evaluate(Policy(completionAction: SharingPolicy.CompletionStop), supportsSharing: false).Action);
    }
}
