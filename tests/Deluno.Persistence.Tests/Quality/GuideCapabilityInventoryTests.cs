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
                "release-title-regex|required|not-negated",
                "typed-source-matcher",
                "advanced-source-matcher",
                "advanced-source-group",
                "advanced-source-profile"
            }));
    }

    [Fact]
    public void Pinned_upstream_inventory_is_complete_and_every_source_item_is_retained()
    {
        var package = GuidePackageCatalog.Current;
        var source = Assert.IsType<GuideSourceInventory>(package.SourceInventory);
        var packageFormats = package.CustomFormats.ToDictionary(
            format => format.TrashId,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(package.Source.UpstreamRevision, source.UpstreamRevision);
        Assert.Equal(478, source.CustomFormats.Count);
        Assert.Equal(78, source.FormatGroups.Count);
        Assert.Equal(62, source.QualityProfiles.Count);
        Assert.Equal(source.CustomFormats.Count, source.CustomFormats
            .Select(format => format.TrashId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

        foreach (var upstreamFormat in source.CustomFormats)
        {
            var retained = Assert.IsType<GuideCustomFormat>(
                packageFormats.GetValueOrDefault(upstreamFormat.TrashId));
            Assert.Contains(upstreamFormat.MediaType, retained.MediaTypes ?? []);
            Assert.Equal(upstreamFormat.SourcePath, retained.SourcePath);
            Assert.Equal(upstreamFormat.MatcherClauses, retained.SourceMatcherClauses);
            Assert.Equal(upstreamFormat.Scores, retained.SourceScores);
        }

        Assert.All(source.FormatGroups.SelectMany(group => group.CustomFormats), entry =>
            Assert.Contains(entry.TrashId, packageFormats.Keys, StringComparer.OrdinalIgnoreCase));
        Assert.All(source.QualityProfiles.SelectMany(profile => profile.FormatAssignments), assignment =>
            Assert.Contains(assignment.TrashId, packageFormats.Keys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unreviewed_upstream_rules_remain_advanced_with_their_native_matcher_and_score_provenance()
    {
        var package = GuidePackageCatalog.Current;
        var source = Assert.IsType<GuideSourceInventory>(package.SourceInventory);
        var upstreamOnly = source.CustomFormats.First(format => !package.CustomFormats.Any(candidate =>
            string.Equals(candidate.TrashId, format.TrashId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.SourceKind, "trash-guides-upstream-advanced", StringComparison.Ordinal)));
        var retained = Assert.Single(package.CustomFormats, format =>
            string.Equals(format.TrashId, upstreamOnly.TrashId, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(GuideMappingStatus.Advanced, retained.MappingStatus);
        Assert.Empty(retained.MappedTraitIds);
        Assert.Empty(retained.Patterns);
        Assert.NotEmpty(retained.SourceMatcherClauses ?? []);
        Assert.NotNull(retained.SourceScores);
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
