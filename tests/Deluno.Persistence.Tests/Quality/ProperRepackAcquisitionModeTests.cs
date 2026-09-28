using Deluno.Integrations.Search;
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

    // #974: every automatic search path (LibrarySearchJobHandler,
    // EpisodeSearchJobHandler, SeasonSearchExecutor, MediaSearchHandler) feeds
    // its plan through ReleaseDecisionEngine.Decide the same way. This is the
    // decision an automatic search actually makes once a Proper of an
    // installed file turns up as a candidate.

    [Fact]
    public void An_automatic_search_passes_over_a_proper_of_an_installed_file_when_upgrades_are_off()
    {
        var plan = BuildPlan(ProperRepackAcquisitionModes.DoNotUpgradeAutomatically);
        var decision = DecideAgainstInstalledFile(plan, "Movie.2026.1080p.WEB-DL.PROPER-GRP");

        // Matched but not upgrade-driving: the installed non-Proper file and
        // the Proper candidate are equivalent, so nothing is grabbed.
        Assert.Equal("equivalent", decision.Status);
        Assert.Equal(PreferenceCandidateStatus.Equivalent, decision.PreferenceComparison?.Status);
    }

    [Fact]
    public void An_automatic_search_takes_a_proper_of_an_installed_file_when_upgrades_are_allowed()
    {
        var plan = BuildPlan(ProperRepackAcquisitionModes.PreferAndUpgrade);
        var decision = DecideAgainstInstalledFile(plan, "Movie.2026.1080p.WEB-DL.PROPER-GRP");

        Assert.Equal("preferred", decision.Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, decision.PreferenceComparison?.Status);
    }

    /// <summary>
    /// Builds the same decision an automatic search makes: an installed file
    /// without the Repack/Proper trait, evaluated against a candidate that
    /// carries it, under the given plan.
    /// </summary>
    private static ReleaseDecision DecideAgainstInstalledFile(ReleasePreferencePlan plan, string candidateReleaseName)
    {
        const string installedReleaseName = "Movie.2026.1080p.WEB-DL-GRP";
        var installedFacts = ReleasePreferenceFactFactory.FromReleaseName(plan, installedReleaseName);
        var snapshot = new PreferenceEvaluationSnapshot(
            MediaId: "movie-1",
            LibraryId: "library-1",
            FileIdentity: "preference-file/v1:movie-1",
            FilePath: $"/library/{installedReleaseName}.mkv",
            FileSizeBytes: 8_000_000_000L,
            PlanId: plan.Id,
            PlanVersion: plan.Version,
            PlanHash: plan.PlanHash,
            Facts: installedFacts,
            Evaluation: ReleasePreferenceEvaluator.Evaluate(plan, installedFacts),
            MatchedRuleIds: [],
            EvaluatedUtc: DateTimeOffset.UnixEpoch,
            Source: "test");

        return ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: candidateReleaseName,
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 8_000_000_000L,
            Seeders: 20,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            PreferencePlan: plan,
            CurrentPreferenceEvaluation: snapshot,
            CurrentFilePresent: true));
    }
}
