using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A per-indexer MultiLanguages setting (#746) says what that indexer's own
/// MULTI tag actually names, e.g. "Original, French". A release tagged MULTI
/// then resolves to those specific languages instead of Deluno's fixed
/// "Multilingual" default, the same way Radarr's per-indexer field works.
/// </summary>
public sealed class IndexerMultiLanguagesFactTests
{
    private static ReleasePreferencePlan FrenchPreferencePlan()
        => ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"]));

    [Fact]
    public void A_multi_tagged_release_resolves_to_the_indexer_specific_languages_when_configured()
    {
        var plan = FrenchPreferencePlan();
        var configuredMulti = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(plan, "Movie.2026.1080p.WEB-DL.MULTI-GRP", multiLanguages: "Original, French"));
        var english = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(plan, "Movie.2026.1080p.WEB-DL.ENGLISH-GRP"));

        // The configured MULTI release resolves to (among others) French, so
        // it outranks a release the language preference does not match — the
        // same comparison the original French-vs-English test makes.
        Assert.True(ReleasePreferenceEvaluator.CompareForSelection(plan, configuredMulti, english) < 0);
    }

    [Fact]
    public void A_multi_tagged_release_keeps_the_fixed_default_when_not_configured()
    {
        var plan = FrenchPreferencePlan();
        var unconfiguredMulti = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(plan, "Movie.2026.1080p.WEB-DL.MULTI-GRP"));
        var french = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(plan, "Movie.2026.1080p.WEB-DL.FRENCH-GRP"));

        // An unconfigured MULTI resolves to the closed-world "Multilingual"
        // bucket, not to French specifically, so an explicitly-French
        // release still outranks it.
        Assert.True(ReleasePreferenceEvaluator.CompareForSelection(plan, french, unconfiguredMulti) < 0);
    }
}
