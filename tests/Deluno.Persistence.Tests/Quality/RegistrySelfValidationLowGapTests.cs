using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The trait registry checks itself, and checks every plan against itself
/// (#1329): each way a registry or a plan can be wrong is named in plain words.
/// </summary>
public sealed class RegistrySelfValidationLowGapTests
{
    private static PreferenceTraitDefinition Trait(string id, string dimension = "d", string name = "N", string[]? aliases = null, string[]? media = null, bool transient = false)
        => new(id, dimension, name, aliases, media, null, null, transient);

    [Fact]
    public void A_registry_names_a_duplicate_alias_a_missing_dimension_or_name_a_cycle_and_a_pair_both_compatible_and_incompatible()
    {
        var registry = new PreferenceTraitRegistry(
            [
                Trait("a.one", aliases: ["shared"]),
                Trait("a.two", aliases: ["shared"]),
                Trait("a.nodim", dimension: " "),
                Trait("a.noname", name: " "),
                Trait("c.x"),
                Trait("c.y")
            ],
            [
                new PreferenceRelationship("c.x", "c.y", PreferenceRelationshipKind.Implies),
                new PreferenceRelationship("c.y", "c.x", PreferenceRelationshipKind.Implies),
                new PreferenceRelationship("a.one", "a.two", PreferenceRelationshipKind.Requires),
                new PreferenceRelationship("a.one", "a.two", PreferenceRelationshipKind.Incompatible)
            ]);

        var errors = registry.Validate();

        Assert.Contains("Preference alias 'shared' belongs to both 'a.one' and 'a.two'.", errors);
        Assert.Contains("Preference trait 'a.nodim' needs a dimension.", errors);
        Assert.Contains("Preference trait 'a.noname' needs a display name.", errors);
        Assert.Contains("Registry implication, requirement, and subsumption relationships must be acyclic.", errors);
        Assert.Contains("Registry relationship pair 'a.one|a.two' is both compatible-by-relationship and incompatible.", errors);
    }

    [Fact]
    public void A_plan_is_refused_a_trait_that_does_not_apply_to_its_media_type_or_sits_in_the_wrong_kind_of_family()
    {
        var registry = new PreferenceTraitRegistry(
        [
            Trait("t.tvonly", media: ["tv"]),
            Trait("t.moving", media: ["both"], transient: true)
        ]);

        var wrongMedia = Plan("movies", persistent: true, "t.tvonly");
        var persistentHoldingTransient = Plan("movies", persistent: true, "t.moving");
        var transientHoldingPersistent = Plan("tv", persistent: false, "t.tvonly");

        Assert.Contains("Trait 't.tvonly' is not applicable to media type 'movies'.", registry.ValidatePlan(wrongMedia));
        Assert.Contains("Trait 't.moving' must be placed in a transient family.", registry.ValidatePlan(persistentHoldingTransient));
        Assert.Contains("Trait 't.tvonly' must be placed in a persistent family.", registry.ValidatePlan(transientHoldingPersistent));
        Assert.Empty(registry.ValidatePlan(Plan("tv", persistent: true, "t.tvonly")));
    }

    private static ReleasePreferencePlan Plan(string mediaType, bool persistent, string traitId)
        => new(
            "plan",
            "1",
            mediaType,
            [new PreferenceFamily("fam", "d", 1, PreferenceIntent.Ranked, [new PreferenceFamilyLevel("lvl", 0, [traitId])], "lvl", Transient: !persistent)]);
}
