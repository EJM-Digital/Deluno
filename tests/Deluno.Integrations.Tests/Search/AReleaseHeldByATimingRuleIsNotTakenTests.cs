using Deluno.Integrations.Search;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// A wait set on the indexer (minimum age) or on a title (availability delay) holds a release back under a profile's typed
/// plan too: it is judged "delayed", not "preferred", so it is not grabbed until the wait has passed. Under a typed plan
/// the outcome used to be rewritten to "preferred" and the wait only left a note, so the release was taken at once. A wait
/// a profile sets itself is proven on a recorded indexer reply in the persistence tests. Refs #1296.
/// </summary>
public sealed class AReleaseHeldByATimingRuleIsNotTakenTests
{
    private static ReleaseDecision Decide(double? ageHours, int? minimumAgeMinutes, int? availabilityDelayDays = null, DateTimeOffset? availableUtc = null, bool rejected = false)
    {
        string[] allowed = ["WEB 1080p", "WEB 2160p"];
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", allowedQualities: allowed, upgradeUntilCutoff: true);
        return ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Some.Film.2020.1080p.WEB-DL.x264-GRP",
            Quality: rejected ? "WEB 480p" : "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 8L * 1024 * 1024 * 1024,
            Seeders: 50,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 0,
            CustomFormatScore: 0,
            AllowedQualities: allowed,
            PreferencePlan: plan,
            CurrentFilePresent: false,
            ReleaseAgeHours: ageHours,
            MinimumAgeMinutes: minimumAgeMinutes,
            AvailableUtc: availableUtc,
            AvailabilityDelayDays: availabilityDelayDays));
    }

    [Fact]
    public void A_release_younger_than_the_indexers_minimum_age_is_delayed_not_preferred_and_is_not_safe_to_send()
    {
        var decision = Decide(ageHours: 0.5, minimumAgeMinutes: 120);

        Assert.Equal("delayed", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Acquisition delay is active", StringComparison.Ordinal));
        Assert.StartsWith("Waiting for its timing rule to clear.", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_old_enough_for_the_indexers_minimum_age_is_preferred_as_before()
    {
        var decision = Decide(ageHours: 5, minimumAgeMinutes: 120);

        Assert.Equal(ReleaseDecisionStatuses.Preferred, decision.Status);
        Assert.DoesNotContain(decision.RiskFlags, flag => flag.Contains("delay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_release_whose_age_is_unknown_cannot_show_it_has_waited_long_enough_so_it_is_delayed()
    {
        var decision = Decide(ageHours: null, minimumAgeMinutes: 120);

        Assert.Equal("delayed", decision.Status);
    }

    [Fact]
    public void A_title_not_yet_available_long_enough_holds_its_release_until_the_availability_delay_has_passed()
    {
        var held = Decide(ageHours: 5, minimumAgeMinutes: null, availabilityDelayDays: 7, availableUtc: DateTimeOffset.UtcNow.AddDays(-2));
        var cleared = Decide(ageHours: 5, minimumAgeMinutes: null, availabilityDelayDays: 7, availableUtc: DateTimeOffset.UtcNow.AddDays(-20));

        Assert.Equal("delayed", held.Status);
        Assert.Equal(ReleaseDecisionStatuses.Preferred, cleared.Status);
    }

    [Fact]
    public void A_release_the_profile_refuses_stays_refused_whatever_the_wait()
    {
        var decision = Decide(ageHours: 0.5, minimumAgeMinutes: 120, rejected: true);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
    }

    [Fact]
    public void A_delayed_release_sorts_after_the_ones_that_can_be_taken()
    {
        Assert.True(ReleaseDecisionStatuses.TypedStageRank("delayed") > ReleaseDecisionStatuses.TypedStageRank(ReleaseDecisionStatuses.Preferred));
    }
}
