using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>Engines-area quality rows that touch nothing outside Deluno. Refs #1296.</summary>
public sealed class EnginesQualityRowsTests
{
    [Fact]
    public void A_release_name_records_the_quality_closed_world_and_every_other_observed_fact_open_world()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", ["WEB 1080p", "Bluray 1080p", "WEB 720p"]);
        var qualityTraits = plan.Families.Single(family => family.Id == "quality").Levels.SelectMany(level => level.NormalizedTraitIds).ToArray();
        Assert.True(qualityTraits.Length >= 3);

        var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, "Dune.2021.1080p.WEB-DL.DDP5.1.Atmos.HDR10.10bit.x265-GRP");

        // the named tier is present, the others of the plan's family are absent, both closed-world
        var named = Assert.Single(facts, fact => fact.State == PreferenceFactState.Present && qualityTraits.Contains(fact.NormalizedTraitId));
        Assert.Equal(PreferenceEvidenceModel.ClosedWorld, named.Evidence!.Model);
        var absent = facts.Where(fact => fact.State == PreferenceFactState.Absent && qualityTraits.Contains(fact.NormalizedTraitId)).ToArray();
        Assert.Equal(qualityTraits.Length - 1, absent.Length);
        Assert.All(absent, fact => Assert.Equal(PreferenceEvidenceModel.ClosedWorld, fact.Evidence!.Model));

        // codec, channels, source, audio, Atmos, HDR, resolution, bit depth: open-world observed
        foreach (var (dimension, value) in new[] { ("video.codec", "x265"), ("audio.channels", "5.1"), ("audio.object", "Atmos"), ("video.dynamic-range", "HDR10"), ("video.resolution", "1080p"), ("video.bit-depth", "10bit") })
        {
            Assert.True(PreferenceTraitRegistry.Current.TryResolveObserved(dimension, value, out var definition), $"{dimension} {value} is in the registry");
            var fact = Assert.Single(facts, item => item.NormalizedTraitId == definition.Id.ToLowerInvariant() && item.State == PreferenceFactState.Present);
            Assert.Equal(PreferenceEvidenceModel.OpenWorld, fact.Evidence!.Model);
        }

        // nothing is invented from a name that says nothing
        Assert.DoesNotContain(ReleasePreferenceFactFactory.FromReleaseName(plan, "Dune"), fact => fact.State == PreferenceFactState.Present);
    }
}
