using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// How a plan's relationships between traits change what a release is judged
/// to carry (#1329): a trait that needs a companion, and two that cannot sit together.
/// </summary>
public sealed class PreferenceRelationshipsLowGapTests
{
    private const string Atmos = "audio.format.truehd-atmos";
    private const string TrueHd = "audio.format.truehd";
    private const string Dts = "audio.format.dts";

    private static ReleasePreferencePlan PlanWith(params PreferenceRelationship[] relationships)
        => new(
            "movies/audio",
            "1",
            "movies",
            [new PreferenceFamily(
                "audio",
                "audio.format",
                1,
                PreferenceIntent.Ranked,
                [new PreferenceFamilyLevel("atmos", 0, [Atmos]), new PreferenceFamilyLevel("truehd", 1, [TrueHd]), new PreferenceFamilyLevel("dts", 2, [Dts])],
                "atmos")],
            Relationships: relationships);

    private static PreferenceFact Present(string trait) => new(trait, PreferenceFactState.Present);

    [Fact]
    public void A_trait_whose_required_companion_is_not_proven_is_in_conflict_and_needs_review()
    {
        var plan = PlanWith(new PreferenceRelationship(Atmos, TrueHd, PreferenceRelationshipKind.Requires));

        var alone = ReleasePreferenceEvaluator.Evaluate(plan, [Present(Atmos)]);
        var withCompanion = ReleasePreferenceEvaluator.Evaluate(plan, [Present(Atmos), Present(TrueHd)]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, alone.Status);
        Assert.Equal(PreferenceFactState.Conflicting, alone.Families[0].State);
        Assert.NotEqual(PreferenceEvaluationStatus.NeedsReview, withCompanion.Status);
        Assert.NotEqual(PreferenceFactState.Conflicting, withCompanion.Families[0].State);
    }

    [Fact]
    public void Two_traits_that_cannot_sit_together_put_both_in_conflict_only_when_both_are_present()
    {
        var plan = PlanWith(new PreferenceRelationship(Atmos, Dts, PreferenceRelationshipKind.Incompatible));

        var both = ReleasePreferenceEvaluator.Evaluate(plan, [Present(Atmos), Present(Dts)]);
        var one = ReleasePreferenceEvaluator.Evaluate(plan, [Present(Atmos)]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, both.Status);
        Assert.Equal(PreferenceFactState.Conflicting, both.Families[0].State);
        Assert.NotEqual(PreferenceEvaluationStatus.NeedsReview, one.Status);
    }

    [Fact]
    public void A_trait_that_subsumes_another_satisfies_a_gate_that_asks_for_the_other()
    {
        var plan = PlanWith(new PreferenceRelationship(Atmos, TrueHd, PreferenceRelationshipKind.Subsumes)) with { RequiredTraitIds = [TrueHd] };
        var unrelated = PlanWith() with { RequiredTraitIds = [TrueHd] };

        var subsumed = ReleasePreferenceEvaluator.Evaluate(plan, [Present(Atmos)]);
        var notSubsumed = ReleasePreferenceEvaluator.Evaluate(unrelated, [Present(Atmos)]);

        Assert.True(subsumed.HardGatesPassed);
        Assert.False(notSubsumed.HardGatesPassed);
    }
}
