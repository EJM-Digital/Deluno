using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

public sealed class ReleaseDecisionEngineQualityModelTests
{
    /// <summary>
    /// A release outside the profile's allowed tiers is usually also a
    /// downgrade, so the two rules meet on the same candidate. The allowed-tier
    /// gate has to survive that meeting: it is the rule that stops a profile
    /// capped at 1080p from taking a 2160p release, and nothing about a typed
    /// plan makes it optional.
    /// </summary>
    [Fact]
    public void A_quality_outside_the_allowed_tiers_stays_rejected_under_a_typed_plan()
    {
        string[] allowed = ["WEB 2160p", "WEB 1080p"];
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies",
            "WEB 2160p",
            allowedQualities: allowed);

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.720p.WEB-DL-GROUP",
            Quality: "WEB 720p",
            CurrentQuality: "WEB 2160p",
            TargetQuality: "WEB 2160p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 42,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            AllowedQualities: allowed,
            PreferencePlan: plan,
            CurrentReleaseName: "/library/Example.mkv",
            CurrentFilePresent: true));

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.RiskFlags, flag =>
            flag.Contains("not one of the qualities", StringComparison.OrdinalIgnoreCase));

        // The downgrade warning is still shown; it just does not decide.
        Assert.Contains(decision.RiskFlags, flag =>
            flag.Contains("Downgrade blocked", StringComparison.OrdinalIgnoreCase));

        // A rejection names the gate that failed, not the preference
        // comparison. Saying "your file is better" about a release the
        // profile refused would name the wrong rule.
        Assert.Contains("not one of the qualities", decision.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installed file is better", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same downgrade with no allowed-tier violation is not a rejection:
    /// under a typed plan the comparator owns that outcome, and it is only
    /// "your file is better" once the installed baseline is known.
    /// </summary>
    [Fact]
    public void A_downgrade_inside_the_allowed_tiers_is_not_rejected_under_a_typed_plan()
    {
        string[] allowed = ["WEB 2160p", "WEB 1080p"];
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies",
            "WEB 2160p",
            allowedQualities: allowed);

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 2160p",
            TargetQuality: "WEB 2160p",
            SizeBytes: 8L * 1024 * 1024 * 1024,
            Seeders: 42,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            AllowedQualities: allowed,
            PreferencePlan: plan,
            CurrentReleaseName: "/library/Example.mkv",
            CurrentFilePresent: true));

        Assert.NotEqual(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.RiskFlags, flag =>
            flag.Contains("Downgrade blocked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_uses_the_size_bounds_the_profile_itself_set()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 20L * 1024 * 1024 * 1024, // 20 GB
            Seeders: 20,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            // #394: the bound is this profile's own answer, not a shared table.
            // 20 GB is comfortably inside where WEB 1080p films normally land,
            // so only a profile that said otherwise can refuse it - which is
            // the whole point of the size answer belonging to the profile.
            SizeRules: [new ProfileSizeRule("WEB 1080p", 1.0, 2.0, 350, 1200)]));

        // Exceeding the ceiling is a rejection rather than a score penalty
        // (#284) - the size answer is described as the final check that
        // rejects a release.
        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag =>
            flag.Contains("above", StringComparison.OrdinalIgnoreCase) &&
            flag.Contains("maximum", StringComparison.OrdinalIgnoreCase));
        Assert.True(decision.SizeScore < 0);
    }

    [Fact]
    public void Decide_honors_the_profiles_own_answer_about_when_to_stop()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 12,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 10,
            NeverGrabPatterns: null,
            CurrentCustomFormatScore: 10,
            // #394: this profile's own answer, not one policy for every shelf.
            UpgradeStop: new QualityUpgradeStopPolicy(true, true)));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Upgrade stop policy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The other half of the same answer, and the reason it belongs to the
    /// profile: a shelf you want chased forever must be able to say so.
    ///
    /// <para>The candidate genuinely improves the upgrade-driving formats, so
    /// the separate equivalent-replacement guard is not in play — that one
    /// refuses a file no better than the one you hold, which is churn under any
    /// policy. What is being tested here is only the profile's own answer about
    /// whether meeting the cutoff ends the search.</para>
    /// </summary>
    [Fact]
    public void A_profile_that_never_stops_keeps_looking_past_the_cutoff()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 12,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 250,
            NeverGrabPatterns: null,
            CurrentCustomFormatScore: 10,
            UpgradeStop: new QualityUpgradeStopPolicy(
                StopWhenCutoffMet: false, RequireCustomFormatGainForSameQuality: false)));

        Assert.NotEqual("rejected", decision.Status);
        Assert.DoesNotContain(decision.RiskFlags, flag => flag.Contains("Upgrade stop policy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same release, the same library, the same held file — refused only
    /// because this profile said meeting the cutoff ends the search. That
    /// difference is the whole of #394's second item.
    /// </summary>
    [Fact]
    public void The_same_improving_release_is_refused_by_a_profile_that_stops()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 12,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 250,
            NeverGrabPatterns: null,
            CurrentCustomFormatScore: 10,
            UpgradeStop: new QualityUpgradeStopPolicy(
                StopWhenCutoffMet: true, RequireCustomFormatGainForSameQuality: false)));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Upgrade stop policy", StringComparison.OrdinalIgnoreCase));
    }
}
