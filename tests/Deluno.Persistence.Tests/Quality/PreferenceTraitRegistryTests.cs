using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class PreferenceTraitRegistryTests
{
    [Fact]
    public void Current_registry_is_unique_and_covers_the_normative_dimensions()
    {
        var registry = PreferenceTraitRegistry.Current;

        Assert.Empty(registry.Validate());
        Assert.True(registry.Traits.Count >= 100);
        Assert.Contains(registry.Traits, trait => trait.Dimension == "quality");
        Assert.Contains(registry.Traits, trait => trait.Dimension == "video.dynamic-range");
        Assert.Contains(registry.Traits, trait => trait.Dimension == "audio.format");
        Assert.Contains(registry.Traits, trait => trait.Dimension == "subtitle");
        Assert.Contains(registry.Traits, trait => trait.Dimension == "release.revision");
        Assert.Contains(registry.Traits, trait => trait.Dimension == "unwanted");
        Assert.Contains(registry.Relationships, relationship => relationship.Kind == PreferenceRelationshipKind.Implies);
        Assert.Contains(registry.Relationships, relationship => relationship.Kind == PreferenceRelationshipKind.CoreOf);
    }

    [Fact]
    public void Aliases_resolve_for_detection_but_plans_require_canonical_ids()
    {
        var registry = PreferenceTraitRegistry.Current;

        Assert.True(registry.TryResolve("TrueHD Atmos", out var definition));
        Assert.Equal("audio.format.truehd-atmos", definition.Id);

        var plan = new ReleasePreferencePlan(
            "movies/audio",
            "1",
            "movies",
            [new PreferenceFamily(
                "audio",
                "Audio",
                1,
                PreferenceIntent.Ranked,
                [new PreferenceFamilyLevel("atmos", 0, ["TrueHD Atmos"])],
                "atmos")]);

        Assert.Contains(registry.ValidatePlan(plan), error => error.Contains("canonical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Current_relationships_allow_the_overlap_examples_without_double_counting()
    {
        var plan = new ReleasePreferencePlan(
            "movies/audio",
            "1",
            "movies",
            [new PreferenceFamily(
                "audio",
                "Audio",
                1,
                PreferenceIntent.Ranked,
                [
                    new PreferenceFamilyLevel("atmos", 0, ["audio.format.truehd-atmos"]),
                    new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                    new PreferenceFamilyLevel("dts-ma", 2, ["audio.format.dts-hd-ma"])
                ],
                "truehd")],
            Relationships: [
                new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies)]);

        Assert.Empty(PreferenceTraitRegistry.Current.ValidatePlan(plan));
        var evaluation = ReleasePreferenceEvaluator.Evaluate(plan, [new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Present)]);
        var family = Assert.Single(evaluation.Families);
        Assert.Equal("atmos", family.SelectedLevelId);
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, evaluation.Status);
    }

    [Fact]
    public void Registry_rejects_a_relationship_that_points_at_an_unknown_trait()
    {
        var registry = new PreferenceTraitRegistry(
            [new PreferenceTraitDefinition("audio.format.aac", "audio.format", "AAC")],
            [new PreferenceRelationship("audio.format.aac", "audio.format.missing", PreferenceRelationshipKind.Implies)]);

        Assert.Contains(registry.Validate(), error => error.Contains("unknown trait", StringComparison.OrdinalIgnoreCase));
    }
}
