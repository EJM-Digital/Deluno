using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #767: a real Proper/Repack acquisition choice instead of the guide's
/// Repack/Proper custom format always scoring the same way. The guide ships
/// Repack/Proper as ordinary custom formats (trash-guide-package.json), so
/// the setting acts where those formats compile into the plan
/// (ReleasePreferencePlanFactory), not as a second competing scoring path.
/// </summary>
public sealed class ProperRepackAcquisitionModeTests
{
    // The guide's real "Repack/Proper" custom format (trash-guide-package.json),
    // reviewed and mapped to release.revision.proper.
    private const string RepackProperTrashId = "e7718d7a3ce595f289bfee26adc178f5";

    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    private static CustomFormatItem RepackProperFormat() => new(
        Id: "repack-proper",
        Name: "Repack/Proper",
        MediaType: "movies",
        Score: 5,
        TrashId: RepackProperTrashId,
        Conditions: "[{\"type\":\"releaseTitle\",\"value\":\"PROPER|REPACK\",\"required\":false}]",
        UpgradeAllowed: true,
        CreatedUtc: Stamp,
        UpdatedUtc: Stamp);

    private static ReleasePreferencePlan BuildPlan(string properRepackMode) =>
        ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies",
            "WEB 1080p",
            ["WEB 1080p"],
            customFormats: [RepackProperFormat()],
            formatIntents: new Dictionary<string, string> { ["repack-proper"] = ProfileFormatIntents.StronglyPrefer },
            properRepackAcquisitionMode: properRepackMode);

    private static PreferenceEvaluation Evaluate(ReleasePreferencePlan plan, string releaseName)
        => ReleasePreferenceEvaluator.Evaluate(plan, ReleasePreferenceFactFactory.FromReleaseName(plan, releaseName));

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.PROPER-GRP")]
    [InlineData("Movie.2026.1080p.WEB-DL.REPACK-GRP")]
    public void Prefer_and_upgrade_lets_the_format_drive_an_automatic_upgrade(string releaseName)
    {
        var plan = BuildPlan(ProperRepackAcquisitionModes.PreferAndUpgrade);

        var family = Assert.Single(plan.Families, family => family.Id == CustomFormatPreferenceTraits.Family("repack-proper"));
        Assert.True(family.UpgradeDriving);

        var evaluation = Evaluate(plan, releaseName);
        Assert.Contains(evaluation.Families, item => item.FamilyId == family.Id && item.State == PreferenceFactState.Present);
    }

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.PROPER-GRP")]
    [InlineData("Movie.2026.1080p.WEB-DL.REPACK-GRP")]
    public void Do_not_upgrade_automatically_still_matches_but_never_drives_an_upgrade(string releaseName)
    {
        var plan = BuildPlan(ProperRepackAcquisitionModes.DoNotUpgradeAutomatically);

        var family = Assert.Single(plan.Families, family => family.Id == CustomFormatPreferenceTraits.Family("repack-proper"));
        Assert.False(family.UpgradeDriving);

        // Still recognised as matched — it can still win a same-search
        // comparison between simultaneously discovered candidates.
        var evaluation = Evaluate(plan, releaseName);
        Assert.Contains(evaluation.Families, item => item.FamilyId == family.Id && item.State == PreferenceFactState.Present);
    }

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.PROPER-GRP")]
    [InlineData("Movie.2026.1080p.WEB-DL.REPACK-GRP")]
    public void Do_not_prefer_drops_the_format_from_the_plan_entirely(string releaseName)
    {
        var plan = BuildPlan(ProperRepackAcquisitionModes.DoNotPrefer);

        Assert.DoesNotContain(plan.Families, family => family.Id == CustomFormatPreferenceTraits.Family("repack-proper"));

        // No family means no reason for this release name to evaluate any
        // differently than one carrying no revision marker at all.
        var evaluation = Evaluate(plan, releaseName);
        Assert.DoesNotContain(evaluation.Families, item => item.FamilyId == CustomFormatPreferenceTraits.Family("repack-proper"));
    }

    [Fact]
    public void An_unrecognized_mode_falls_back_to_prefer_and_upgrade()
    {
        Assert.Equal(ProperRepackAcquisitionModes.PreferAndUpgrade, ProperRepackAcquisitionModes.Normalize("not-a-real-mode"));
        Assert.Equal(ProperRepackAcquisitionModes.PreferAndUpgrade, ProperRepackAcquisitionModes.Normalize(null));
    }
}
