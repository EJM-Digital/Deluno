using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

public sealed class GuideCapabilityInventoryTests
{
    [Fact]
    public void Every_shipped_guide_item_has_a_typed_or_explicit_advanced_representation()
    {
        var inventory = GuideCapabilityInventoryBuilder.Build(GuidePackageCatalog.Current);

        Assert.Empty(inventory.Unaccounted);
        Assert.True(inventory.TotalItemCount >= GuidePackageCatalog.Current.CustomFormats.Count);
        Assert.True(inventory.TypedItemCount > 0);
        Assert.True(inventory.AdvancedItemCount > 0);
        Assert.All(inventory.Items, item =>
            Assert.Contains(item.Representation, new[]
            {
                "typed-trait",
                "typed-forbidden",
                "advanced-legacy-matcher",
                "typed-plan",
                "typed-plan+advanced",
                "typed-bundle",
                "typed-bundle+advanced",
                "release-title-regex|required|not-negated"
            }));
    }

    [Fact]
    public void Inventory_hash_is_stable_and_tracks_the_source_package()
    {
        var first = GuideCapabilityInventoryBuilder.Build(GuidePackageCatalog.Current);
        var second = GuideCapabilityInventoryBuilder.Build(GuidePackageCatalog.Current);

        Assert.Equal(first.InventoryHash, second.InventoryHash);
        Assert.Equal(first.PackageIntegritySha256, GuidePackageCatalog.Current.IntegritySha256);
        Assert.Equal(GuidePackageCatalog.Current.Source.UpstreamRevision, first.SourceRevision);
    }
}
