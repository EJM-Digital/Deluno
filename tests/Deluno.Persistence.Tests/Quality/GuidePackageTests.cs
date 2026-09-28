using System.Text.Json;
using Deluno.Quality;
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

    /// <summary>
    /// #987: the owner wants TRaSH's own HDR/Dolby Vision formats promoted from
    /// Advanced to reviewed, using TRaSH's own scores, so #938's plain
    /// quality choices actually use them instead of only the four Deluno
    /// invented HDR10/HDR10+/Dolby Vision/HLG replacements.
    /// </summary>
    [Theory]
    [InlineData("493b6d1dbec3c3364c59d7607f7e3405", "HDR")]
    [InlineData("505d871304820ba7106b693be6fe4a9e", "HDR")]
    [InlineData("b337d6812e06c200ec9a2d3cfa9d20a7", "DV Boost")]
    [InlineData("7c3a61a9c6cb04f52f1544be6d44a026", "DV Boost")]
    [InlineData("caa37d0df9c348912df1fb1d88f9273a", "HDR10+ Boost")]
    [InlineData("0c4b99df9206d2cfac3c05ab897dd62a", "HDR10+ Boost")]
    [InlineData("923b6abef9b17f937fab56cfcf89e1f1", "DV (w/o HDR fallback)")]
    [InlineData("9b27ab6498ec0f31a3353992e19434ca", "DV (w/o HDR fallback)")]
    [InlineData("f700d29429c023a5734505e77daeaea7", "DV (Disk)")]
    [InlineData("ef4963043b0987f8485bc9106f16db38", "DV (Disk)")]
    [InlineData("9c38ebb7384dada637be8899efa68e6f", "SDR")]
    [InlineData("2016d1676f5ee13a5b7257ff86ac9a93", "SDR")]
    public void Hdr_and_dolby_vision_formats_are_reviewed_with_a_typed_mapping(string trashId, string name)
    {
        var package = GuidePackageCatalog.Current;
        var format = Assert.Single(package.CustomFormats, item => item.TrashId == trashId);

        Assert.Equal(name, format.Name);
        Assert.Equal("hdr", format.Category);
        Assert.Equal(GuideMappingStatus.Reviewed, format.MappingStatus);
        Assert.NotEmpty(format.MappedTraitIds);
        Assert.All(format.MappedTraitIds, traitId =>
            Assert.True(PreferenceTraitRegistry.Current.TryResolve(traitId, out _), $"'{traitId}' is not a known trait."));
    }

    /// <summary>
    /// #987: a release with "DV (w/o HDR fallback)" must score in the
    /// compiled plan exactly as TRaSH's own web-2160p profile scores it
    /// (-10000), and, now that it is reviewed, must be represented by the
    /// typed "no HDR fallback" trait rather than left as an Advanced-only
    /// legacy rule.
    /// </summary>
    [Fact]
    public void Dv_without_hdr_fallback_scores_as_trash_says_and_is_typed_not_advanced()
    {
        const string trashId = "923b6abef9b17f937fab56cfcf89e1f1";
        var package = GuidePackageCatalog.Current;
        var format = Assert.Single(package.CustomFormats, item => item.TrashId == trashId);
        Assert.Equal(-10000, format.OriginalScore);

        var compilation = GuidePlanCompiler.Compile("web-2160p", "movies", package);
        var source = Assert.Single(compilation.Plan.Sources!, item => item.SourceId == trashId);

        Assert.Equal("-10000", source.OriginalScore);
        Assert.Equal("-10000", source.AssignedScore);
        Assert.DoesNotContain(compilation.AdvancedRules, rule => rule.TrashId == trashId);

        var dynamicRange = compilation.Plan.Families.Single(family => family.Dimension == "HDR / dynamic range");
        Assert.Contains(dynamicRange.Levels, level => level.TraitIds.Contains("video.dynamic-range.no-hdr-fallback"));
    }

    [Theory]
    [InlineData("french")]
    [InlineData("german")]
    [InlineData("dutch")]
    [InlineData("asian")]
    [InlineData("uk")]
    public void Regional_upstream_formats_are_reachable_through_a_named_category(string category)
    {
        var package = GuidePackageCatalog.Current;

        var regional = package.CustomFormats.Where(format => format.Category == category).ToArray();

        Assert.NotEmpty(regional);
        Assert.All(regional, format =>
        {
            Assert.Equal("trash-guides-upstream-advanced", format.SourceKind);
            Assert.NotEqual("anime", format.Category);
        });
    }

    /// <summary>
    /// #781: a fresh anime profile is meant to get the anime release-group
    /// tiers and the Anime Versions (v0-v4) set without a user hunting for
    /// them under Advanced. The frontend's "apply a starter" flow
    /// (ensureRecommendedFormats) attaches whatever a guide quality profile
    /// recommends, so the promise is only real if the "anime-1080p" starter's
    /// own recommendedFormats actually names every one of those formats.
    /// </summary>
    [Fact]
    public void The_anime_starter_recommends_every_anime_release_group_tier_and_version_tag()
    {
        var package = GuidePackageCatalog.Current;
        var animeStarter = package.QualityProfiles.Single(profile => profile.Id == "anime-1080p");
        var recommendedIds = animeStarter.RecommendedFormats.Select(item => item.TrashId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var animeDefaultGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[Release Groups] Anime",
            "[Required] Anime Versions"
        };
        var animeDefaultTrashIds = (package.SourceInventory!.FormatGroups ?? [])
            .Where(group => animeDefaultGroupNames.Contains(group.Name))
            .SelectMany(group => group.CustomFormats ?? [])
            .Select(format => format.TrashId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(animeDefaultTrashIds);
        Assert.All(animeDefaultTrashIds, trashId => Assert.Contains(trashId, recommendedIds));
    }

    /// <summary>
    /// A non-anime starter must never pick up these formats by the same
    /// mechanism, or every profile would quietly gain anime scoring.
    /// </summary>
    [Fact]
    public void A_non_anime_starter_does_not_recommend_the_anime_default_formats()
    {
        var package = GuidePackageCatalog.Current;
        var webStarter = package.QualityProfiles.Single(profile => profile.Id == "web-1080p");
        var recommendedIds = webStarter.RecommendedFormats.Select(item => item.TrashId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var animeDefaultGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[Release Groups] Anime",
            "[Required] Anime Versions"
        };
        var animeDefaultTrashIds = (package.SourceInventory!.FormatGroups ?? [])
            .Where(group => animeDefaultGroupNames.Contains(group.Name))
            .SelectMany(group => group.CustomFormats ?? [])
            .Select(format => format.TrashId);

        Assert.All(animeDefaultTrashIds, trashId => Assert.DoesNotContain(trashId, recommendedIds));
    }

    [Fact]
    public void Regional_categories_do_not_swallow_the_dedicated_anime_group()
    {
        var package = GuidePackageCatalog.Current;

        // "FR Anime Tier 01" belongs only to the upstream "[Release Groups]
        // French Anime" group, which is deliberately excluded from the
        // regional map: promoting anime tiers by default is #781's job, not a
        // regional split.
        var frenchAnimeTier = Assert.Single(package.CustomFormats,
            format => format.TrashId == "03520acbc5e46ce326be984534792d28");
        Assert.Equal("upstream-advanced", frenchAnimeTier.Category);
    }

    [Theory]
    [InlineData("fb3ccc5d5cc8f77c9055d4cb4561dded", "Anime BD Tier 01")]
    [InlineData("6115ccd6640b978234cc47f2c1f2cadc", "Anime BD Tier 08")]
    [InlineData("a786fbc0eae05afe3bb51aee3c83a9d4", "Anime Web Tier 06")]
    [InlineData("b0fdc5897f68c9a68c70c25169f77447", "Anime LQ Groups")]
    [InlineData("c259005cbaeb5ab44c06eddb4751e70c", "v0")]
    [InlineData("d4e5e842fad129a3c097bdb2d20d31a0", "v4")]
    public void Anime_release_group_tiers_and_version_tags_are_enabled_by_default_only_for_the_anime_profile(
        string trashId,
        string name)
    {
        var animeCompilation = GuidePlanCompiler.Compile("anime-1080p");
        Assert.Contains(animeCompilation.AdvancedRules, rule => rule.TrashId == trashId);

        var moviesCompilation = GuidePlanCompiler.Compile("web-1080p");
        Assert.DoesNotContain(moviesCompilation.AdvancedRules, rule => rule.TrashId == trashId);
        Assert.DoesNotContain(moviesCompilation.Plan.Sources ?? [], source => source.SourceId == trashId);

        // Sanity check the fixture names the trashId we expect.
        Assert.Equal(name, GuidePackageCatalog.Current.CustomFormats.Single(format => format.TrashId == trashId).Name);
    }

    [Theory]
    [InlineData("unwanted.cam", "Movie.Title.2024.CAM.x264-GROUP")]
    [InlineData("unwanted.telesync", "Movie.Title.2024.TELESYNC.x264-GROUP")]
    [InlineData("unwanted.telecine", "Movie.Title.2024.TELECINE.x264-GROUP")]
    [InlineData("unwanted.r5", "Movie.Title.2024.R5.x264-GROUP")]
    [InlineData("unwanted.screener", "Movie.Title.2024.SCREENER.x264-GROUP")]
    [InlineData("unwanted.vhsrip", "Movie.Title.2024.VHSRip.x264-GROUP")]
    [InlineData("unwanted.workprint", "Movie.Title.2024.WORKPRINT.x264-GROUP")]
    public void Classic_low_quality_sources_are_forbidden_by_default_and_reject_a_matching_release_name(
        string traitId,
        string releaseName)
    {
        var compilation = GuidePlanCompiler.Compile("web-1080p");

        Assert.Contains(traitId, compilation.Plan.ForbiddenTraitIds!);

        var source = compilation.Plan.Sources!.Single(item =>
            (item.MappedTraitIds ?? []).Contains(traitId, StringComparer.OrdinalIgnoreCase));
        var match = CustomFormatDefinitionMatcher.Evaluate(
            releaseName,
            source.MatcherDefinition,
            source.MatcherAny,
            compilation.Plan.MediaType);

        Assert.True(match.IsMatch, $"Expected '{releaseName}' to match custom format for '{traitId}'.");

        var cleanRelease = "Movie.Title.2024.BluRay.REMUX.x264-GROUP";
        var cleanMatch = CustomFormatDefinitionMatcher.Evaluate(
            cleanRelease,
            source.MatcherDefinition,
            source.MatcherAny,
            compilation.Plan.MediaType);
        Assert.False(cleanMatch.IsMatch, $"Expected '{cleanRelease}' not to match custom format for '{traitId}'.");
    }

    [Fact]
    public void Runtime_plan_factory_uses_the_same_best_first_order_as_the_guide_compiler()
    {
        var formats = GuidePackageCatalog.Current.CustomFormats
            .Where(format => format.MappingStatus == GuideMappingStatus.Reviewed
                && GuideFormatConditions.AppliesTo(format, "movies")
                && format.MappedTraitIds.Any(trait => trait.StartsWith("audio.format.", StringComparison.Ordinal)))
            .Select(format => new Deluno.Quality.Contracts.CustomFormatItem(
                format.TrashId,
                format.Name,
                "movies",
                format.OriginalScore,
                format.TrashId,
                GuideFormatConditions.For(format),
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
                && GuideFormatConditions.AppliesTo(format, "movies")
                && format.MappedTraitIds.Contains("audio.format.truehd"));
        var timestamp = DateTimeOffset.UtcNow;
        var format = new Deluno.Quality.Contracts.CustomFormatItem(
            "selected-truehd",
            guideFormat.Name,
            "movies",
            999999,
            guideFormat.TrashId,
            GuideFormatConditions.For(guideFormat),
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
            timestamp)
        {
            // The profile's own answer. The rule's score, however large, no
            // longer stands in for one: a rule nobody has turned on is off (#550).
            FormatIntents = new Dictionary<string, string> { [format.Id] = ProfileFormatIntents.StronglyPrefer }
        };

        var plan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(profile, [format]);
        var audio = Assert.Single(plan.Families, family => family.Dimension == "audio.format");
        var customRule = Assert.Single(plan.Families, family => family.Dimension == $"Custom rule · {format.Name}");

        Assert.Equal(Deluno.Quality.ReleasePreferences.PreferenceIntent.Neutral, audio.Intent);
        Assert.False(audio.UpgradeDriving);
        Assert.Equal(Deluno.Quality.ReleasePreferences.PreferenceIntent.Ranked, customRule.Intent);
        Assert.True(customRule.UpgradeDriving);
        Assert.Equal("matched", customRule.TargetLevelId);
        Assert.DoesNotContain(plan.Families.SelectMany(family => family.Levels)
            .SelectMany(level => level.TraitIds), traitId =>
            traitId.Contains("999999", StringComparison.Ordinal));
        Assert.Contains(plan.Sources!, source =>
            source.AssignedScore == "999999"
            && source.RuleId == format.Id
            && source.ProfileIntent == ProfileFormatIntents.StronglyPrefer);
    }

    /// <summary>
    /// #550: a rule is off until a person turns it on. A rule the guide files
    /// under "unwanted" used to be forced to "Never" in any profile that held
    /// it, even over an answer the person had given. The guide's view is now
    /// the suggestion, and the profile's own answer is what acts.
    /// </summary>
    [Fact]
    public void An_unwanted_guide_rule_is_a_forbidden_gate_once_the_profile_says_never_and_off_until_then()
    {
        // Any reviewed, movies-scoped unwanted format exercises this
        // scenario; #780 grew the baseline unwanted set past one entry.
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.First(
            format => format.MappingStatus == GuideMappingStatus.Reviewed
                && GuideFormatConditions.AppliesTo(format, "movies")
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
            GuideFormatConditions.For(guideFormat),
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

        // In the profile, with no answer: off. It forbids nothing, and the guide only suggests.
        var offPlan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(
            qualityProfile,
            [customFormat]);
        Assert.DoesNotContain(Deluno.Quality.ReleasePreferences.CustomFormatPreferenceTraits.Matched(customFormat.Id), offPlan.ForbiddenTraitIds ?? []);
        Assert.Equal(ProfileFormatIntents.Off, Assert.Single(offPlan.Sources!, source => source.RuleId == customFormat.Id).ProfileIntent);
        Assert.Equal(ProfileFormatIntents.MustNotHave, ProfileFormatIntents.SuggestionFor(guideFormat.OriginalScore, guideFormat.Category));

        qualityProfile = qualityProfile with
        {
            FormatIntents = new Dictionary<string, string> { [customFormat.Id] = ProfileFormatIntents.MustNotHave }
        };
        var runtimePlan = Deluno.Quality.ReleasePreferences.ReleasePreferencePlanFactory.CreateQualityPlan(
            qualityProfile,
            [customFormat]);

        var source = Assert.Single(runtimePlan.Sources!, source => source.RuleId == customFormat.Id);
        Assert.Contains(Deluno.Quality.ReleasePreferences.CustomFormatPreferenceTraits.Matched(customFormat.Id), runtimePlan.ForbiddenTraitIds!);
        Assert.Equal(guideFormat.TrashId, source.MappingId?.Split(':').Last());

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
            fact.Evidence?.Detail?.Contains(customFormat.Name, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Forbidden_guide_traits_include_more_specific_relationship_members()
    {
        var forbidden = GuidePlanCompiler.ExpandForbiddenTraits(["audio.format.truehd"]);

        Assert.Contains("audio.format.truehd", forbidden);
        Assert.Contains("audio.format.truehd-atmos", forbidden);
    }
}
