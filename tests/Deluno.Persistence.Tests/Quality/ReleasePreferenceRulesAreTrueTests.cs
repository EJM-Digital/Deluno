using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The rules of the typed release-preference evaluation, one at a time (coverage register, engines): a forbidden trait that is unknown
/// needs review instead of passing, facts that disagree resolve to Conflicting, an implied trait proven absent beside its present
/// specific trait is Conflicting, a family's state is Present, Unknown, Absent or Conflicting by what is proven, a candidate failing a
/// gate is Rejected when the trait is proven absent and NeedsReview when it is unknown, and the validator refuses a plan that
/// cannot be judged. Refs #1296.
/// </summary>
public sealed class ReleasePreferenceRulesAreTrueTests
{
    private static PreferenceFamily Quality(string intent = "ranked", bool upgradeDriving = true)
        => new(
            "quality", "Quality", 1,
            intent == "ranked" ? PreferenceIntent.Ranked : PreferenceIntent.TieBreak,
            [new PreferenceFamilyLevel("bluray", 0, ["quality.bluray-1080p"]), new PreferenceFamilyLevel("web", 1, ["quality.web-1080p"])],
            TargetLevelId: intent == "ranked" ? "bluray" : null,
            UpgradeDriving: upgradeDriving);

    private static ReleasePreferencePlan Plan(params PreferenceFamily[] families) => new("movies/rules", "1", "movies", families.Length == 0 ? [Quality()] : families);

    private static PreferenceFact Fact(string trait, PreferenceFactState state, string? source = null)
        => new(trait, state, source is null ? null : new PreferenceEvidence(source, Detail: source));

    [Fact]
    public void A_forbidden_trait_that_is_unknown_needs_review_one_that_is_proven_absent_passes_and_one_that_is_present_is_missing()
    {
        var plan = Plan() with { ForbiddenTraitIds = ["audio.format.dts"] };
        PreferenceFact good = Fact("quality.bluray-1080p", PreferenceFactState.Present);

        var unknown = ReleasePreferenceEvaluator.Evaluate(plan, [good]);
        var absent = ReleasePreferenceEvaluator.Evaluate(plan, [good, Fact("audio.format.dts", PreferenceFactState.Absent)]);
        var present = ReleasePreferenceEvaluator.Evaluate(plan, [good, Fact("audio.format.dts", PreferenceFactState.Present)]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, unknown.Status);
        Assert.Contains(unknown.Reasons, reason => reason.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, absent.Status);
        Assert.Equal(PreferenceEvaluationStatus.Missing, present.Status);
    }

    [Fact]
    public void Facts_for_one_trait_from_sources_that_disagree_resolve_to_Conflicting_and_a_conflicting_upgrade_driving_family_needs_review()
    {
        var plan = Plan();

        var evaluation = ReleasePreferenceEvaluator.Evaluate(plan,
        [
            Fact("quality.bluray-1080p", PreferenceFactState.Present, "release name"),
            Fact("quality.bluray-1080p", PreferenceFactState.Absent, "probe")
        ]);

        var family = Assert.Single(evaluation.Families);
        Assert.Equal(PreferenceFactState.Conflicting, family.State);
        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, evaluation.Status);
    }

    [Fact]
    public void An_implied_trait_that_is_proven_absent_while_its_specific_trait_is_present_becomes_Conflicting()
    {
        var plan = new ReleasePreferencePlan(
            "movies/implied", "1", "movies",
            [new PreferenceFamily("audio", "Audio", 1, PreferenceIntent.Ranked,
                [new PreferenceFamilyLevel("truehd", 0, ["audio.format.truehd"]), new PreferenceFamilyLevel("dts", 1, ["audio.format.dts"])], TargetLevelId: "truehd")],
            Relationships: [new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies)],
            RequiredTraitIds: ["audio.format.truehd-atmos"]);

        var contradicted = ReleasePreferenceEvaluator.Evaluate(plan,
        [
            Fact("audio.format.truehd-atmos", PreferenceFactState.Present),
            Fact("audio.format.truehd", PreferenceFactState.Absent)
        ]);
        var agreeing = ReleasePreferenceEvaluator.Evaluate(plan, [Fact("audio.format.truehd-atmos", PreferenceFactState.Present)]);

        Assert.Equal(PreferenceFactState.Conflicting, Assert.Single(contradicted.Families).State);
        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, contradicted.Status);
        Assert.Equal(PreferenceFactState.Present, Assert.Single(agreeing.Families).State);
    }

    [Fact]
    public void A_family_is_Present_when_a_level_is_proven_Unknown_when_a_better_level_is_unproven_or_nothing_is_proven_and_Absent_when_everything_is_proven_absent()
    {
        var plan = Plan();

        var present = Assert.Single(ReleasePreferenceEvaluator.Evaluate(plan, [Fact("quality.bluray-1080p", PreferenceFactState.Present), Fact("quality.web-1080p", PreferenceFactState.Absent)]).Families);
        var betterUnproven = Assert.Single(ReleasePreferenceEvaluator.Evaluate(plan, [Fact("quality.web-1080p", PreferenceFactState.Present)]).Families);
        var nothingProven = Assert.Single(ReleasePreferenceEvaluator.Evaluate(plan, []).Families);
        var allAbsent = Assert.Single(ReleasePreferenceEvaluator.Evaluate(plan, [Fact("quality.bluray-1080p", PreferenceFactState.Absent), Fact("quality.web-1080p", PreferenceFactState.Absent)]).Families);

        Assert.Equal(PreferenceFactState.Present, present.State);
        Assert.Equal(PreferenceFactState.Unknown, betterUnproven.State);
        Assert.Equal(PreferenceFactState.Unknown, nothingProven.State);
        Assert.Equal(PreferenceFactState.Absent, allAbsent.State);
    }

    [Fact]
    public void A_candidate_failing_a_gate_on_proven_absence_is_Rejected_and_on_unknown_evidence_needs_review()
    {
        var plan = Plan() with { RequiredTraitIds = ["audio.channels.5-1"] };
        PreferenceFact[] installed = [Fact("quality.bluray-1080p", PreferenceFactState.Present), Fact("audio.channels.5-1", PreferenceFactState.Present)];

        var absent = ReleasePreferenceEvaluator.Compare(plan, installed, [Fact("quality.bluray-1080p", PreferenceFactState.Present), Fact("audio.channels.5-1", PreferenceFactState.Absent)]);
        var unknown = ReleasePreferenceEvaluator.Compare(plan, installed, [Fact("quality.bluray-1080p", PreferenceFactState.Present)]);

        Assert.Equal(PreferenceCandidateStatus.Rejected, absent.Status);
        Assert.Equal(PreferenceCandidateStatus.NeedsReview, unknown.Status);
    }

    [Fact]
    public void The_validator_refuses_a_plan_missing_its_identity_and_a_family_that_cannot_be_judged()
    {
        PreferenceFamilyLevel Level(string id, params string[] traits) => new(id, 0, traits);

        Assert.Contains("Plan id is required.", ReleasePreferencePlanValidator.Validate(new ReleasePreferencePlan("", "1", "movies", [])));
        Assert.Contains("Plan version is required.", ReleasePreferencePlanValidator.Validate(new ReleasePreferencePlan("p", " ", "movies", [])));
        Assert.Contains("Plan media type is required.", ReleasePreferencePlanValidator.Validate(new ReleasePreferencePlan("p", "1", "", [])));

        var noLevels = ReleasePreferencePlanValidator.Validate(Plan(new PreferenceFamily("empty", "Quality", 1, PreferenceIntent.Ranked, [], UpgradeDriving: false)));
        var noDimension = ReleasePreferencePlanValidator.Validate(Plan(new PreferenceFamily("nodim", "", 1, PreferenceIntent.Ranked, [Level("a", "trait.a")], UpgradeDriving: false)));
        var unknownIntent = ReleasePreferencePlanValidator.Validate(Plan(new PreferenceFamily("odd", "Quality", 1, (PreferenceIntent)99, [Level("a", "trait.a")], UpgradeDriving: false)));
        var twoFamilies = ReleasePreferencePlanValidator.Validate(Plan(
            new PreferenceFamily("one", "Quality", 1, PreferenceIntent.Ranked, [Level("a", "trait.shared")], UpgradeDriving: false),
            new PreferenceFamily("two", "Audio", 2, PreferenceIntent.Ranked, [Level("b", "trait.shared")], UpgradeDriving: false)));
        var twoLevels = ReleasePreferencePlanValidator.Validate(Plan(
            new PreferenceFamily("one", "Quality", 1, PreferenceIntent.Ranked, [Level("a", "trait.shared"), Level("b", "trait.shared")], UpgradeDriving: false)));
        var unknownTarget = ReleasePreferencePlanValidator.Validate(Plan(
            new PreferenceFamily("one", "Quality", 1, PreferenceIntent.Ranked, [Level("a", "trait.a")], TargetLevelId: "nowhere", UpgradeDriving: false)));
        var tieBreakDriving = ReleasePreferencePlanValidator.Validate(Plan(
            new PreferenceFamily("tie", "Seeders", 1, PreferenceIntent.TieBreak, [Level("a", "trait.a")], UpgradeDriving: true)));

        Assert.Contains(noLevels, error => error.Contains("must declare at least one level"));
        Assert.Contains(noDimension, error => error.Contains("needs a dimension"));
        Assert.Contains(unknownIntent, error => error.Contains("unknown preference intent"));
        Assert.Contains(twoFamilies, error => error.Contains("counted by both"));
        Assert.Contains(twoLevels, error => error.Contains("appears in multiple levels"));
        Assert.Contains(unknownTarget, error => error.Contains("targets unknown level 'nowhere'"));
        Assert.Contains(tieBreakDriving, error => error.Contains("cannot use TieBreak intent to drive upgrades"));
    }
}
