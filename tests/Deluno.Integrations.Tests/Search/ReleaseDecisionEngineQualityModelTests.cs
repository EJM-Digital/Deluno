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
    public void Decide_uses_structured_quality_model_size_bounds()
    {
        var model = new QualityModelSnapshot(
            Version: "test",
            Tiers:
            [
                new QualityTierDefinition("WEB 1080p", 70, 1.0, 2.0, 350, 1200, 50)
            ],
            UpgradeStop: new QualityUpgradeStopPolicy(true, true),
            UpdatedUtc: DateTimeOffset.UtcNow);

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 20L * 1024 * 1024 * 1024, // 20 GB
            Seeders: 20,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0), model);

        // The bound comes from the model's tier (max 2.0 GB), and exceeding it is
        // now a rejection rather than a score penalty (#284) — the Size Rules
        // screen describes it as the final check that rejects a release.
        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag =>
            flag.Contains("above", StringComparison.OrdinalIgnoreCase) &&
            flag.Contains("maximum", StringComparison.OrdinalIgnoreCase));
        Assert.True(decision.SizeScore < 0);
    }

    [Fact]
    public void Decide_honors_upgrade_stop_policy_when_cutoff_met()
    {
        var model = new QualityModelSnapshot(
            Version: "test",
            Tiers:
            [
                new QualityTierDefinition("WEB 1080p", 70, 1.0, 20.0, 350, 3000, 50)
            ],
            UpgradeStop: new QualityUpgradeStopPolicy(true, true),
            UpdatedUtc: DateTimeOffset.UtcNow);

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
            CurrentCustomFormatScore: 10), model);

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Upgrade stop policy", StringComparison.OrdinalIgnoreCase));
    }
}
