using System.Text.Json;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class GuidePackageTests
{
    [Fact]
    public void Shipped_package_is_valid_and_integrity_hash_is_deterministic()
    {
        var package = GuidePackageCatalog.Current;

        Assert.Empty(GuidePackageCatalog.Validate(package));
        Assert.False(string.IsNullOrWhiteSpace(package.IntegritySha256));
        Assert.Equal(package.IntegritySha256, GuidePackageCatalog.ComputeIntegritySha256(package));
    }

    [Fact]
    public void Schema_v1_package_remains_readable_without_a_source_inventory()
    {
        var current = GuidePackageCatalog.Current;
        var legacy = current with
        {
            Version = 1,
            SchemaVersion = 1,
            SourceInventory = null,
            IntegritySha256 = null
        };

        Assert.Empty(GuidePackageCatalog.Validate(legacy));
        Assert.Empty(GuideCapabilityInventoryBuilder.Build(legacy).Unaccounted);
    }

    [Fact]
    public void Shipped_package_contains_reviewed_typed_mappings_and_explicit_advanced_fallbacks()
    {
        var package = GuidePackageCatalog.Current;

        Assert.True(package.CustomFormats.Count >= 478);
        Assert.True(package.QualityProfiles.Count >= 6);
        Assert.True(package.Bundles.Count >= 6);
        Assert.NotNull(package.SourceInventory);
        Assert.Contains(package.CustomFormats, format => format.MappingStatus == GuideMappingStatus.Reviewed);
        Assert.Contains(package.CustomFormats, format => format.MappingStatus == GuideMappingStatus.Advanced);
        Assert.All(package.CustomFormats.Where(format => format.MappingStatus == GuideMappingStatus.Advanced),
            format => Assert.Empty(format.MappedTraitIds));
        Assert.All(package.CustomFormats.Where(format => format.MappingStatus == GuideMappingStatus.Reviewed),
            format => Assert.NotEmpty(format.MappedTraitIds));
    }

    [Fact]
    public void Guide_profile_recommendations_and_bundles_only_reference_shipped_formats()
    {
        var package = GuidePackageCatalog.Current;
        var formatIds = package.CustomFormats.Select(format => format.TrashId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(package.QualityProfiles.SelectMany(profile => profile.RecommendedFormats),
            recommendation => Assert.Contains(recommendation.TrashId, formatIds));
        Assert.All(package.Bundles.SelectMany(bundle => bundle.Includes),
            entry => Assert.Contains(entry.TrashId, formatIds));
    }

    [Fact]
    public void Guide_profile_compiles_to_a_complete_typed_plan_without_using_scores_as_decisions()
    {
        var compilation = GuidePlanCompiler.Compile("bluray-1080p");

        Assert.Equal("guide/trash-guides/bluray-1080p", compilation.Plan.Id);
        Assert.Equal("movies", compilation.Plan.MediaType);
        Assert.Equal("bluray-1080p", compilation.Plan.Families.Single(family => family.Id == "quality").TargetLevelId);
        Assert.Contains(compilation.Plan.Families, family => family.Intent == Deluno.Quality.ReleasePreferences.PreferenceIntent.TieBreak);
        Assert.NotEmpty(compilation.AdvancedRules);
        Assert.All(compilation.AdvancedRules, rule =>
            Assert.Contains("numeric score", rule.Explanation, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(compilation.Plan.Sources!, source =>
            source.SourceKind == "trash-guide-package"
            && source.SourceVersion.Contains(GuidePackageCatalog.Current.Source.UpstreamRevision, StringComparison.Ordinal));
        Assert.Empty(Deluno.Quality.ReleasePreferences.ReleasePreferencePlanValidator.Validate(compilation.Plan));
    }

    [Fact]
    public void Every_shipped_guide_profile_compiles_with_its_provenance_and_explicit_advanced_boundary()
    {
        var package = GuidePackageCatalog.Current;

        foreach (var profile in package.QualityProfiles)
        {
            var compilation = GuidePlanCompiler.Compile(profile.Id, profile.MediaType, package);

            Assert.Equal(profile.Id, compilation.Profile.Id);
            Assert.Equal($"{package.Version}:{package.Source.UpstreamRevision}", compilation.Plan.Version);
            Assert.Contains(compilation.Plan.Sources!, source =>
                source.SourceKind == "trash-guide-package"
                && source.SourceId == package.Id
                && source.SourceVersion == $"{package.Version}:{package.Source.UpstreamRevision}");
            Assert.Empty(Deluno.Quality.ReleasePreferences.ReleasePreferencePlanValidator.Validate(compilation.Plan));

            // A format is either represented by a reviewed typed trait or is
            // retained as an explicit Advanced rule. It must never disappear
            // merely because a different profile references it.
            var referencedFormats = profile.RecommendedFormats
                .Select(item => item.TrashId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var representedFormats = compilation.Plan.Sources!
                .Select(source => source.SourceId)
                .Concat(compilation.AdvancedRules.Select(rule => rule.TrashId ?? rule.RuleId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(referencedFormats, formatId => Assert.Contains(formatId, representedFormats));
        }
    }

    [Fact]
    public void Guide_compilation_json_exposes_the_canonical_plan_hash_alongside_the_typed_plan()
    {
        var compilation = GuidePlanCompiler.Compile("web-1080p");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            compilation,
            ReleasePreferenceJson.Options));

        var root = document.RootElement;
        Assert.Equal(compilation.PlanHash, root.GetProperty("planHash").GetString());
        Assert.Equal(compilation.Plan.Id, root.GetProperty("plan").GetProperty("id").GetString());
        Assert.DoesNotContain("planHash", root.GetProperty("plan").EnumerateObject()
            .Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guide_compilation_requires_review_when_the_package_contains_a_translation_warning()
    {
        var package = GuidePackageCatalog.Current;
        var profile = package.QualityProfiles.First();
        var changedProfile = profile with
        {
            QualityOrder = [.. profile.QualityOrder ?? [], "missing-quality-tier"]
        };
        var changedPackage = package with { QualityProfiles = [changedProfile] };

        var compilation = GuidePlanCompiler.Compile(
            changedProfile.Id,
            changedProfile.MediaType,
            changedPackage);

        Assert.Contains(compilation.Warnings, warning =>
            warning.Contains("missing quality tier", StringComparison.OrdinalIgnoreCase));
        Assert.True(compilation.RequiresReview);
    }

    [Fact]
    public void Reviewed_audio_mappings_use_relationships_and_tie_breaks_do_not_create_upgrades()
    {
        var compilation = GuidePlanCompiler.Compile("remux-2160p");
        var audio = compilation.Plan.Families.SingleOrDefault(family =>
            family.Dimension == "Audio format");

        Assert.NotNull(audio);
        Assert.Equal(Deluno.Quality.ReleasePreferences.PreferenceIntent.TieBreak, audio!.Intent);
        Assert.False(audio.UpgradeDriving);
        Assert.Contains(compilation.Plan.Relationships!, relationship =>
            relationship.FromTraitId == "audio.format.truehd-atmos"
            && relationship.ToTraitId == "audio.format.truehd");
    }

    [Fact]
    public void Runtime_plan_factory_uses_the_same_best_first_order_as_the_guide_compiler()
    {
        var formats = GuidePackageCatalog.Current.CustomFormats
            .Where(format => format.MappingStatus == GuideMappingStatus.Reviewed
                && format.MappedTraitIds.Any(trait => trait.StartsWith("audio.format.", StringComparison.Ordinal)))
            .Select(format => new Deluno.Quality.Contracts.CustomFormatItem(
                format.TrashId,
                format.Name,
                "movies",
                format.OriginalScore,
                format.TrashId,
                string.Join("\n", format.Patterns),
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow))
            .ToArray();

        var plan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(
            "movies",
            "WEB 1080p",
            ["WEB 1080p"],
            customFormats: formats);
        var audio = Assert.Single(plan.Families, family => family.Dimension == "audio.format");

        Assert.Equal(
            [
                "audio-format-truehd-atmos",
                "audio-format-dtsx",
                "audio-format-truehd",
                "audio-format-dts-hd-ma",
                "audio-format-eac3-atmos",
                "audio-format-eac3",
                "audio-format-dts",
                "audio-format-flac",
                "audio-format-pcm",
                "audio-format-aac",
                "audio-format-opus"
            ],
            audio.OrderedLevels.Select(level => level.Id).ToArray());
    }

    [Fact]
    public void Runtime_plan_factory_honours_upgrade_allowed_without_using_the_custom_format_score()
    {
        var guideFormat = Assert.Single(GuidePackageCatalog.Current.CustomFormats,
            format => format.MappingStatus == GuideMappingStatus.Reviewed
                && format.MappedTraitIds.Contains("audio.format.truehd"));
        var timestamp = DateTimeOffset.UtcNow;
        var format = new Deluno.Quality.Contracts.CustomFormatItem(
            "selected-truehd",
            guideFormat.Name,
            "movies",
            999999,
            guideFormat.TrashId,
            string.Join("\n", guideFormat.Patterns),
            true,
            timestamp,
            timestamp);
        var profile = new Deluno.Quality.Contracts.QualityProfileItem(
            "profile-with-audio",
            "Premium audio",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            format.Id,
            true,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);

        var plan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(profile, [format]);
        var audio = Assert.Single(plan.Families, family => family.Dimension == "audio.format");

        Assert.Equal(Deluno.Quality.ReleasePreferences.PreferenceIntent.Ranked, audio.Intent);
        Assert.True(audio.UpgradeDriving);
        Assert.Equal("audio-format-truehd", audio.TargetLevelId);
        Assert.DoesNotContain(plan.Families.SelectMany(family => family.Levels)
            .SelectMany(level => level.TraitIds), traitId =>
            traitId.Contains("999999", StringComparison.Ordinal));
        Assert.Contains(plan.Sources!, source => source.AssignedScore == "999999");
    }

    [Fact]
    public void Reviewed_unwanted_guide_mapping_becomes_a_forbidden_gate()
    {
        var guideFormat = Assert.Single(GuidePackageCatalog.Current.CustomFormats,
            format => format.MappingStatus == GuideMappingStatus.Reviewed
                && string.Equals(format.Category, "unwanted", StringComparison.OrdinalIgnoreCase));
        var profile = GuidePackageCatalog.Current.QualityProfiles
            .First(item => string.Equals(item.MediaType, "movies", StringComparison.OrdinalIgnoreCase))
            with
            {
                RecommendedFormats = [new GuideRecommendedFormat(guideFormat.TrashId, guideFormat.OriginalScore)]
            };
        var package = GuidePackageCatalog.Current with { QualityProfiles = [profile] };

        var guideCompilation = GuidePlanCompiler.Compile(profile.Id, profile.MediaType, package);

        Assert.Contains(guideCompilation.Plan.ForbiddenTraitIds!, traitId =>
            guideFormat.MappedTraitIds.Contains(traitId, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(guideCompilation.Plan.Families.SelectMany(family => family.Levels)
            .SelectMany(level => level.TraitIds), traitId =>
            guideFormat.MappedTraitIds.Contains(traitId, StringComparer.OrdinalIgnoreCase));

        var timestamp = DateTimeOffset.UtcNow;
        var customFormat = new Deluno.Quality.Contracts.CustomFormatItem(
            "selected-unwanted",
            guideFormat.Name,
            profile.MediaType,
            guideFormat.OriginalScore,
            guideFormat.TrashId,
            string.Join("\n", guideFormat.Patterns),
            true,
            timestamp,
            timestamp);
        var qualityProfile = new Deluno.Quality.Contracts.QualityProfileItem(
            "profile-with-safety-gate",
            "Safety gate",
            profile.MediaType,
            "WEB 1080p",
            "WEB 1080p",
            customFormat.Id,
            true,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);

        var runtimePlan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(
            qualityProfile,
            [customFormat]);

        Assert.All(guideFormat.MappedTraitIds, traitId =>
            Assert.Contains(traitId, runtimePlan.ForbiddenTraitIds!));
        Assert.Contains(runtimePlan.Sources!, source => source.SourceId == guideFormat.TrashId);

        var snapshot = Deluno.Quality.ReleasePreferences.InstalledPreferenceEvaluationFactory.Create(
            qualityProfile,
            "movie-1",
            "library-1",
            "Movie.2026.1080p.Upscaled.mkv",
            100,
            "WEB 1080p",
            timestamp,
            "test",
            [customFormat]);

        Assert.NotNull(snapshot);
        Assert.Equal(Deluno.Quality.ReleasePreferences.PreferenceEvaluationStatus.Missing, snapshot!.Evaluation.Status);
        Assert.All(guideFormat.MappedTraitIds, traitId =>
            Assert.Contains(snapshot.Facts, fact =>
                fact.TraitId.Equals(traitId, StringComparison.OrdinalIgnoreCase)
                && fact.State == Deluno.Quality.ReleasePreferences.PreferenceFactState.Present));
        Assert.Contains(customFormat.Id, snapshot.MatchedRuleIds);
        Assert.Contains(snapshot.Facts, fact =>
            fact.Evidence?.Detail?.Contains(guideFormat.TrashId, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Forbidden_guide_traits_include_more_specific_relationship_members()
    {
        var forbidden = GuidePlanCompiler.ExpandForbiddenTraits(["audio.format.truehd"]);

        Assert.Contains("audio.format.truehd", forbidden);
        Assert.Contains("audio.format.truehd-atmos", forbidden);
    }
}
