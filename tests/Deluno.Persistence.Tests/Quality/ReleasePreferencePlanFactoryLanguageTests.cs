using Deluno.Quality.Contracts;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// "Preferred language" as a Ranked/Required rule in the typed preference
/// system (#769): a matching release ranks above a non-matching one, and
/// "require" refuses a release explicitly tagged as a different language
/// without refusing an untagged one.
/// </summary>
public sealed class ReleasePreferencePlanFactoryLanguageTests
{
    private static PreferenceEvaluation Evaluate(ReleasePreferencePlan plan, string releaseName)
        => ReleasePreferenceEvaluator.Evaluate(plan, ReleasePreferenceFactFactory.FromReleaseName(plan, releaseName));

    [Fact]
    public void No_language_preference_adds_no_family()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"], languagePreference: null);

        Assert.DoesNotContain(plan.Families, family => family.Id == "language-preference");
    }

    [Fact]
    public void A_matching_language_ranks_above_a_non_matching_one()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"]));

        var french = Evaluate(plan, "Movie.2026.1080p.WEB-DL.FRENCH-GRP");
        var english = Evaluate(plan, "Movie.2026.1080p.WEB-DL.ENGLISH-GRP");

        // Negative: the left candidate (French) sorts first, i.e. wins the
        // selection - the same comparer the search planner sorts candidates
        // with (FeedMediaSearchPlanner.BuildPlanAsync).
        Assert.True(ReleasePreferenceEvaluator.CompareForSelection(plan, french, english) < 0);
    }

    [Fact]
    public void Preferring_a_language_never_replaces_an_installed_file_on_its_own()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"]));

        var family = Assert.Single(plan.Families, family => family.Id == "language-preference");
        Assert.False(family.UpgradeDriving);
    }

    [Fact]
    public void Requiring_a_language_refuses_a_release_explicitly_tagged_as_a_different_one()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"], RequireLanguage: true));

        var german = Evaluate(plan, "Movie.2026.1080p.WEB-DL.GERMAN-GRP");

        Assert.Equal(PreferenceEvaluationStatus.Missing, german.Status);
    }

    [Fact]
    public void Requiring_a_language_does_not_refuse_a_release_that_names_no_language_at_all()
    {
        // Most legitimate releases in a person's own language carry no
        // language marker at all - only foreign or multi releases usually do.
        // A hard gate that read that silence as "wrong language" would refuse
        // the overwhelming majority of correct releases.
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"], RequireLanguage: true));

        var untagged = Evaluate(plan, "Movie.2026.1080p.WEB-DL-GRP");

        Assert.NotEqual(PreferenceEvaluationStatus.Missing, untagged.Status);
    }

    [Fact]
    public void Requiring_a_language_still_accepts_a_release_explicitly_tagged_as_that_language()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            languagePreference: new ProfileLanguagePreference(["fr"], RequireLanguage: true));

        var french = Evaluate(plan, "Movie.2026.1080p.WEB-DL.FRENCH-GRP");

        Assert.NotEqual(PreferenceEvaluationStatus.Missing, french.Status);
    }

    [Fact]
    public void A_language_a_chosen_custom_format_already_maps_stays_with_that_format_and_the_preference_orders_only_the_rest()
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        // The shipped "Dual Audio" format is reviewed to the typed trait audio.language.multi.
        const string trashId = "4a3b087eea2ce012fcc1ce319259a3be";
        var format = new CustomFormatItem(
            "dual-audio", "Dual Audio", "movies", 0, trashId,
            "[{\"implementation\":\"ReleaseTitleSpecification\",\"negate\":false,\"required\":true,\"fields\":{\"value\":\"\\bdual\\b\"}}]",
            true, now, now);

        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies", "WEB 1080p", ["WEB 1080p"],
            customFormats: [format],
            formatIntents: new Dictionary<string, string> { [format.Id] = ProfileFormatIntents.Prefer },
            languagePreference: new ProfileLanguagePreference(["multi", "fr"]));

        var language = Assert.Single(plan.Families, family => family.Id == "language-preference");
        var levelTraits = language.Levels.SelectMany(level => level.TraitIds).ToArray();
        Assert.DoesNotContain("audio.language.multi", levelTraits);
        Assert.Contains("audio.language.fr", levelTraits);
        Assert.Empty(ReleasePreferencePlanValidator.Validate(plan));
    }
}
