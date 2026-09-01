using Deluno.Quality.Scenarios;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Quality;

public sealed class MediaPlanScenarioTests
{
    [Fact]
    public void Catalog_contains_the_approved_scenario_first_choices_and_explicit_applicability()
    {
        var expected = new[]
        {
            "family-1080p",
            "premium-4k-hdr",
            "low-storage",
            "usenet-first",
            "private-tracker",
            "mixed-sources",
            "anime"
        };

        Assert.Equal(expected, MediaPlanScenarioCatalog.All.Select(scenario => scenario.Id));
        Assert.All(MediaPlanScenarioCatalog.All, scenario =>
        {
            Assert.NotEmpty(scenario.Requirements);
            Assert.NotEmpty(scenario.Variants);
            Assert.All(scenario.Variants, variant =>
            {
                Assert.Contains(variant.MediaType, new[] { "movies", "tv" });
                Assert.True(variant.SearchIntervalHours > 0);
                Assert.True(variant.RetryDelayHours > 0);
                Assert.NotEmpty(variant.SizeTierId);
                Assert.NotEmpty(variant.Summary);
            });
        });

        Assert.Equal(["tv"], MediaPlanScenarioCatalog.Find("anime")!.MediaTypes);
        Assert.Equal(["movies", "tv"], MediaPlanScenarioCatalog.Find("family-1080p")!.MediaTypes);
    }

    [Fact]
    public void Compiler_is_deterministic_and_uses_the_existing_policy_set_path()
    {
        var first = MediaPlanScenarioCompiler.Compile("premium-4k-hdr", "movies");
        var second = MediaPlanScenarioCompiler.Compile("premium-4k-hdr", "movies");

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal("movies", first.PolicySet.MediaType);
        Assert.Equal("4k-movies", first.QualityPresetId);
        Assert.Equal(6, first.PolicySet.SearchIntervalOverrideHours);
        Assert.Equal(3, first.PolicySet.RetryDelayOverrideHours);
        Assert.True(first.PolicySet.UpgradeUntilCutoff);
        Assert.Contains("Scenario: premium-4k-hdr v1", first.PolicySet.Notes);
        Assert.Contains(first.IncludedBehaviors, behavior => behavior.StartsWith("Size tier:", StringComparison.Ordinal));
        Assert.Contains(first.IncludedBehaviors, behavior => behavior.StartsWith("Routing:", StringComparison.Ordinal));
        Assert.Contains(first.Behaviors!, behavior =>
            behavior.Id == "search-cadence" && behavior.ApplicationStatus == "applied");
        Assert.Contains(first.Behaviors!, behavior =>
            behavior.Id == "size" && behavior.ApplicationStatus == "requires-configuration");
        Assert.Contains(first.Behaviors!, behavior =>
            behavior.Id == "sharing-retention" && behavior.ApplicationStatus == "informational");
        Assert.DoesNotContain(first.Behaviors!, behavior =>
            behavior.ApplicationStatus is not ("applied" or "requires-configuration" or "informational"));
    }

    [Fact]
    public void Compiler_requires_a_media_type_for_dual_scope_and_rejects_incompatible_scope()
    {
        var missing = Assert.Throws<ArgumentException>(() => MediaPlanScenarioCompiler.Compile("family-1080p"));
        Assert.Contains("both Movies and TV", missing.Message, StringComparison.Ordinal);

        var incompatible = Assert.Throws<ArgumentException>(() => MediaPlanScenarioCompiler.Compile("anime", "movies"));
        Assert.Contains("does not apply", incompatible.Message, StringComparison.Ordinal);

        var aliases = MediaPlanScenarioCompiler.Compile("anime", "TV shows");
        Assert.Equal("tv", aliases.MediaType);
    }
}
