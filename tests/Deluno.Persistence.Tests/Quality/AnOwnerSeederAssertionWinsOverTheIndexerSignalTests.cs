using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class AnOwnerSeederAssertionWinsOverTheIndexerSignalTests
{
    private static ReleasePreferencePlan MoviePlan()
        => ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", ["WEB 1080p"]);

    [Fact]
    public void A_seeder_fact_the_caller_already_supplied_is_kept_and_no_second_state_is_added()
    {
        var plan = MoviePlan();
        var supplied = new PreferenceFact("transient.seeders.none", PreferenceFactState.Present);

        var facts = ReleasePreferenceFactFactory.WithTransientSignals(plan, [supplied], seeders: 25);

        var seederFacts = facts.Where(f => f.TraitId.StartsWith("transient.seeders.", StringComparison.Ordinal)).ToList();
        var only = Assert.Single(seederFacts);
        Assert.Equal("transient.seeders.none", only.TraitId);
        Assert.Equal(PreferenceFactState.Present, only.State);
    }

    [Fact]
    public void Without_a_supplied_seeder_fact_the_indexer_signal_marks_the_matching_level_present_and_the_other_absent()
    {
        var plan = MoviePlan();

        var facts = ReleasePreferenceFactFactory.WithTransientSignals(plan, null, seeders: 25);

        Assert.Equal(PreferenceFactState.Present, facts.Single(f => f.TraitId == "transient.seeders.available").State);
        Assert.Equal(PreferenceFactState.Absent, facts.Single(f => f.TraitId == "transient.seeders.none").State);
    }
}
