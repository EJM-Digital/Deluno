using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// For a title with nothing on disk, only the quality cutoff can hold a release.
///
/// <para><b>Why this file exists.</b> #550, decided by the owner on 2026-09-19.
/// Before the soak the rig held every episode of a series it had none of, for
/// review, because the profile strongly preferred a release group the episodes
/// were not from: "usable, but it does not meet every preference yet". His
/// 17 September decision - below the cutoff is held, even for a missing title -
/// had been applied to every target a profile has, not only to quality. A
/// preference a release misses still ranks it below one that has it, and can
/// still justify an upgrade once there is a file to improve on.</para>
/// </summary>
public sealed class AMissingTitleIsTakenAtTheCutoffTests
{
    [Fact]
    public void A_release_at_the_cutoff_that_misses_a_strongly_preferred_rule_is_taken()
    {
        var decision = Decide("Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO", "WEB 1080p");

        Assert.Equal("preferred", decision.Status);
        Assert.True(decision.MeetsCutoff);
        Assert.Contains(decision.Reasons, reason => reason.Contains("Nothing is on disk yet", StringComparison.Ordinal));
        // The miss is still said, because it is still true and still ranks the release.
        Assert.Contains(decision.Reasons, reason => reason.Contains("Trusted group is below its target", StringComparison.Ordinal));
    }

    /// <summary>The 17 September decision stands: below the cutoff waits for a person.</summary>
    [Fact]
    public void A_release_below_the_cutoff_is_still_held()
    {
        var decision = Decide("Pioneer.One.S01E01.720p.WEB-DL.DDP5.1.H.264-DELUNO", "WEB 720p");

        Assert.Equal("eligible", decision.Status);
        Assert.False(decision.MeetsCutoff);
    }

    [Fact]
    public void A_release_that_has_the_preferred_rule_is_preferred_without_the_exception()
    {
        var decision = Decide("Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-TRUSTED", "WEB 1080p");

        Assert.Equal("preferred", decision.Status);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("Nothing is on disk yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of #550: a rule is off until a person turns it on. The rig's
    /// "WEB Tier 02" scored 1650 in the guide, nobody had answered it, and it
    /// compiled to a target all the same. A guide sync that moved a score would
    /// have changed what every such profile did.
    /// </summary>
    [Fact]
    public void A_rule_nobody_has_turned_on_sets_no_target_whatever_the_guide_scores_it()
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        var tier = new CustomFormatItem("web-tier-02", "WEB Tier 02", "tv", 1650, null,
            """[{"type":"releaseGroup","value":"TRUSTED","negate":false,"required":true}]""", true, timestamp, timestamp);
        var never = new CustomFormatItem("x265-hd", "x265 (HD)", "tv", -10000, null,
            """[{"type":"releaseGroup","value":"HEVCGROUP","negate":false,"required":true}]""", true, timestamp, timestamp);
        var profile = new QualityProfileItem("tv-1080p", "TV 1080p", "tv", "WEB 1080p", "WEB 720p, WEB 1080p",
            $"{tier.Id}, {never.Id}", true, false, false, null, null, false, timestamp, timestamp);

        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(profile, [tier, never]);

        foreach (var rule in new[] { tier, never })
        {
            var family = Assert.Single(plan.Families, item => item.Dimension == $"Custom rule · {rule.Name}");
            Assert.Equal(PreferenceIntent.Neutral, family.Intent);
            Assert.Null(family.TargetLevelId);
            Assert.False(family.UpgradeDriving);
            Assert.Equal(ProfileFormatIntents.Off, Assert.Single(plan.Sources!, source => source.RuleId == rule.Id).ProfileIntent);
        }

        Assert.Empty(plan.ForbiddenTraitIds ?? []);
        // What the guide would suggest is still there to be shown beside each rule.
        Assert.Equal(ProfileFormatIntents.StronglyPrefer, ProfileFormatIntents.SuggestionFor(tier.Score, null));
        Assert.Equal(ProfileFormatIntents.MustNotHave, ProfileFormatIntents.SuggestionFor(never.Score, null));
    }

    private static ReleaseDecision Decide(string releaseName, string quality)
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        var trustedGroup = new CustomFormatItem(
            "trusted-group",
            "Trusted group",
            "tv",
            1650,
            null,
            """[{"type":"releaseGroup","value":"TRUSTED","negate":false,"required":true}]""",
            true,
            timestamp,
            timestamp);
        var profile = new QualityProfileItem(
            "tv-1080p",
            "TV 1080p",
            "tv",
            "WEB 1080p",
            "WEB 720p, WEB 1080p",
            trustedGroup.Id,
            true,
            false,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp)
        {
            FormatIntents = new Dictionary<string, string> { [trustedGroup.Id] = ProfileFormatIntents.StronglyPrefer }
        };
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(profile, [trustedGroup]);

        return ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: releaseName,
            Quality: quality,
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 2_500_000_000,
            Seeders: 25,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            PreferencePlan: plan,
            CurrentFilePresent: false));
    }
}
