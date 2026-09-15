using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Creates the typed quality policy used by acquisition when a persisted
/// profile has not yet been compiled into a guide-backed plan. Every valid
/// selected custom-format matcher becomes an immutable Boolean decision family;
/// only missing or empty matchers remain in Advanced diagnostics. Reviewed
/// safety mappings are compiled as forbidden hard gates and never behave like
/// a positive preference.
/// </summary>
public static class ReleasePreferencePlanFactory
{
    /// <summary>
    /// Builds the effective immutable plan for one persisted quality profile.
    /// The profile's selected custom-format rows are the only rows considered;
    /// an inventory-wide custom-format list must not silently change a title's
    /// plan.
    /// </summary>
    public static ReleasePreferencePlan CreateQualityPlan(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        guidePackage ??= GuidePackageCatalog.Current;
        var selected = SelectProfileFormats(profile, customFormats);
        var inputsVersion = $"profile/{profile.UpdatedUtc.ToUniversalTime():yyyyMMddHHmmssfffffff}/"
            + $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}";
        var compiled = CreateQualityPlan(
            profile.MediaType,
            profile.CutoffQuality,
            Split(profile.AllowedQualities),
            profile.UpgradeUntilCutoff,
            id: $"quality-profile/{profile.Id}",
            version: inputsVersion,
            customFormats: selected,
            guidePackage: guidePackage,
            formatIntents: profile.FormatIntents);

        // The profile's saved time and the guide package do not say what the
        // plan was compiled into, and plans are immutable per version. Found on
        // the physical rig after an upgrade: an older build had stored this
        // profile's plan under this same version, the newer build compiled a
        // different definition, and every read of the profile's plan failed
        // with "is immutable and already contains a different definition". A
        // rule edited without re-saving the profile collides the same way.
        //
        // So the definition names itself. An unchanged definition keeps its
        // version across restarts; a changed one gets a new version beside the
        // old, and anything recorded under the old one still resolves to exactly
        // the plan that decided it.
        return compiled with { Version = $"{inputsVersion}/def-{compiled.PlanHash[..16]}" };
    }

    /// <summary>
    /// Returns the typed plan plus explicit review items that cannot be
    /// evaluated safely. A valid local or unreviewed matcher is compiled as a
    /// plan-scoped matched/not-matched family, so its typed owner intent works
    /// without turning its legacy numeric score into decision arithmetic.
    /// </summary>
    public static RuntimeQualityProfileCompilation CompileProfile(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        guidePackage ??= GuidePackageCatalog.Current;
        var selected = SelectProfileFormats(profile, customFormats);
        var selectedIds = Split(profile.CustomFormatIds);
        var availableById = (customFormats ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var advanced = new List<LegacyPreferenceRuleTranslation>();
        var warnings = new List<string>();

        foreach (var id in selectedIds)
        {
            if (!availableById.TryGetValue(id, out var format))
            {
                advanced.Add(new LegacyPreferenceRuleTranslation(
                    id,
                    id,
                    null,
                    MediaPolicyCatalog.NormalizeMediaType(profile.MediaType),
                    0,
                    true,
                    string.Empty,
                    LegacyPreferenceRuleKind.Invalid,
                    null,
                    true,
                    "The quality profile references a custom format that is not present in the current inventory."));
                warnings.Add($"Custom format '{id}' is referenced but missing from the inventory.");
                continue;
            }

            if (!CustomFormatDefinitionMatcher.CanEvaluate(format.Conditions))
            {
                var explanation = string.IsNullOrWhiteSpace(format.Conditions)
                    ? "The custom format has no matcher conditions, so Deluno cannot prove whether a release matches it."
                    : "The custom format contains a condition Deluno cannot interpret completely, so it remains review-only rather than changing the rule.";
                advanced.Add(new LegacyPreferenceRuleTranslation(
                    format.Id,
                    format.Name,
                    format.TrashId,
                    MediaPolicyCatalog.NormalizeMediaType(profile.MediaType),
                    format.Score,
                    format.UpgradeAllowed,
                    format.Conditions,
                    LegacyPreferenceRuleKind.UnmappedAdvanced,
                    null,
                    true,
                    explanation));
                warnings.Add($"Custom format '{format.Name}' cannot be evaluated completely and needs review.");
            }
        }

        var plan = CreateQualityPlan(profile, selected, guidePackage);
        return new RuntimeQualityProfileCompilation(
            profile,
            plan,
            advanced,
            warnings,
            advanced.Count > 0);
    }

    /// <summary>
    /// The tiers a quality family has to be able to place, best first.
    ///
    /// <para>Not the same list as "which tiers may be grabbed". The allowed
    /// list is a gate on candidates and <see cref="Deluno.Integrations"/>'s
    /// decision engine enforces it directly; this list decides which installed
    /// files the plan can recognise at all. Building it from the allowed list
    /// alone meant a held file <em>better</em> than every allowed tier matched
    /// no level, read as below goal, and asked to be replaced by something
    /// worse - a profile allowing up to Bluray 1080p would have wanted to
    /// downgrade your Bluray 2160p file. So every catalogue tier at or above
    /// the cutoff is included, which is exactly the set the engine being
    /// replaced calls "cutoff met".</para>
    /// </summary>
    public static IReadOnlyList<string> QualityFamilyTiers(
        IReadOnlyList<string> normalizedAllowed,
        string cutoff)
    {
        if (normalizedAllowed.Count == 0)
        {
            return MediaPolicyCatalog.Current.QualityRanks
                .OrderByDescending(item => item.Value)
                .Select(item => item.Key)
                .ToArray();
        }

        var cutoffRank = MediaPolicyCatalog.Current.GetRank(cutoff);
        return normalizedAllowed
            .Concat([cutoff])
            .Concat(MediaPolicyCatalog.Current.QualityRanks
                .Where(item => item.Value >= cutoffRank)
                .Select(item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(MediaPolicyCatalog.Current.GetRank)
            .ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ReleasePreferencePlan CreateQualityPlan(
        string? mediaType,
        string? targetQuality,
        IReadOnlyList<string>? allowedQualities = null,
        bool upgradeUntilCutoff = true,
        string? id = null,
        string? version = null,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null,
        IReadOnlyDictionary<string, string>? formatIntents = null)
    {
        guidePackage ??= GuidePackageCatalog.Current;
        var normalizedMediaType = MediaPolicyCatalog.NormalizeMediaType(mediaType);
        var cutoff = MediaPolicyCatalog.Current.NormalizeQuality(targetQuality)
            ?? MediaPolicyCatalog.Current.DefaultCutoffQuality;
        var normalizedAllowed = (allowedQualities ?? [])
            .Select(MediaPolicyCatalog.Current.NormalizeQuality)
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var qualityNames = QualityFamilyTiers(normalizedAllowed, cutoff);

        var levels = qualityNames
            .Select((quality, index) => new PreferenceFamilyLevel(
                Id: Slug(quality),
                Rank: index,
                TraitIds: [InstalledPreferenceEvaluationFactory.QualityTraitId(quality)]))
            .ToArray();

        var qualityFamily = new PreferenceFamily(
            Id: "quality",
            Dimension: "Quality",
            Order: 1,
            Intent: PreferenceIntent.Ranked,
            Levels: levels,
            TargetLevelId: Slug(cutoff),
            UpgradeDriving: upgradeUntilCutoff,
            Transient: false);

        var guideFormats = guidePackage.CustomFormats
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var mappedTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forbiddenTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customFormatFamilies = new List<PreferenceFamily>();
        var sources = new List<PreferencePlanProvenance>();
        foreach (var format in customFormats ?? [])
        {
            if (string.IsNullOrWhiteSpace(format.Id)
                || !CustomFormatDefinitionMatcher.CanEvaluate(format.Conditions))
            {
                continue;
            }

            guideFormats.TryGetValue(format.TrashId ?? string.Empty, out var guideFormat);
            var hasReviewedMapping = guideFormat?.MappingStatus == GuideMappingStatus.Reviewed;
            var answer = formatIntents is not null && formatIntents.TryGetValue(format.Id, out var stated)
                ? ProfileFormatIntents.Normalize(stated)
                : ProfileFormatIntents.FromGuideScore(format.Score);
            if (hasReviewedMapping && IsForbiddenCategory(guideFormat!.Category))
            {
                answer = ProfileFormatIntents.MustNotHave;
            }

            var matchedTrait = CustomFormatPreferenceTraits.Matched(format.Id);
            var notMatchedTrait = CustomFormatPreferenceTraits.NotMatched(format.Id);
            var semanticTraits = hasReviewedMapping
                ? PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat!.MappedTraitIds)
                : [];

            sources.Add(new PreferencePlanProvenance(
                SourceKind: CustomFormatPreferenceTraits.SourceKind,
                SourceId: format.Id,
                SourceVersion: format.UpdatedUtc.ToUniversalTime().ToString("O"),
                OriginalScore: (hasReviewedMapping ? guideFormat!.OriginalScore : format.Score)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssignedScore: format.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MappingId: hasReviewedMapping ? $"{guidePackage.Id}:{guideFormat!.TrashId}" : null,
                MappingVersion: CustomFormatPreferenceTraits.MappingVersion,
                Layer: "runtime-quality",
                MatcherDefinition: format.Conditions,
                MappedTraitIds: [matchedTrait, notMatchedTrait, .. semanticTraits],
                MatcherAny: !string.IsNullOrWhiteSpace(format.TrashId),
                RuleId: format.Id,
                RuleName: format.Name,
                ProfileIntent: answer));

            if (ProfileFormatIntents.Refuses(answer))
            {
                forbiddenTraits.Add(matchedTrait);
            }
            else
            {
                var preferMatch = answer is ProfileFormatIntents.Prefer or ProfileFormatIntents.StronglyPrefer;
                var ruleLevels = preferMatch
                    ? new[]
                    {
                        new PreferenceFamilyLevel("matched", 0, [matchedTrait]),
                        new PreferenceFamilyLevel("not-matched", 1, [notMatchedTrait])
                    }
                    : new[]
                    {
                        new PreferenceFamilyLevel("not-matched", 0, [notMatchedTrait]),
                        new PreferenceFamilyLevel("matched", 1, [matchedTrait])
                    };
                var drivesUpgrade = ProfileFormatIntents.DrivesUpgrade(answer) && format.UpgradeAllowed;
                customFormatFamilies.Add(new PreferenceFamily(
                    Id: CustomFormatPreferenceTraits.Family(format.Id),
                    Dimension: $"Custom rule · {format.Name}",
                    Order: customFormatFamilies.Count + 2,
                    Intent: answer == ProfileFormatIntents.DoNotCare
                        ? PreferenceIntent.Neutral
                        : drivesUpgrade ? PreferenceIntent.Ranked : PreferenceIntent.TieBreak,
                    Levels: ruleLevels,
                    TargetLevelId: drivesUpgrade ? "matched" : null,
                    UpgradeDriving: drivesUpgrade,
                    Transient: false));
            }

            foreach (var traitId in semanticTraits)
            {
                if (PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
                    && !trait.Transient)
                {
                    mappedTraits.Add(trait.Id);
                }
            }
        }

        var relationships = PreferenceTraitRegistry.Current.Relationships
            .Where(relationship => mappedTraits.Contains(relationship.FromTraitId)
                && PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out _))
            .ToArray();
        foreach (var relationship in relationships)
        {
            if (PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out var target)
                && !target.Transient)
            {
                mappedTraits.Add(target.Id);
            }
        }

        var mappedFamilies = new List<PreferenceFamily>();
        foreach (var group in mappedTraits
                     .Select(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
                         ? trait
                         : null)
                     .Where(trait => trait is not null)
                     .Cast<PreferenceTraitDefinition>()
                     .GroupBy(trait => trait.Dimension, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var familyLevels = GuidePlanCompiler.OrderTraitLevels(
                group.Key,
                group.Select(trait => trait.Id));
            if (familyLevels.Count == 0) continue;

            mappedFamilies.Add(new PreferenceFamily(
                Id: $"guide.{Slug(group.Key)}",
                Dimension: group.Key,
                Order: mappedFamilies.Count + customFormatFamilies.Count + 2,
                Intent: PreferenceIntent.Neutral,
                Levels: familyLevels,
                TargetLevelId: null,
                UpgradeDriving: false,
                Transient: false));
        }

        var persistentFamilies = new[] { qualityFamily }
            .Concat(customFormatFamilies)
            .Concat(mappedFamilies)
            .ToArray();
        var allFamilies = persistentFamilies
            .Concat([CreateSeederAvailabilityFamily(persistentFamilies.Length + 1)])
            .ToArray();
        var effectiveVersion = version
            ?? (sources.Count > 0
                ? $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}"
                : $"{MediaPolicyCatalog.CurrentVersion}/typed-quality/v3");

        return new ReleasePreferencePlan(
            Id: id ?? $"runtime-quality/{normalizedMediaType}/{Slug(cutoff)}",
            Version: effectiveVersion,
            MediaType: normalizedMediaType,
            Families: allFamilies,
            ForbiddenTraitIds: GuidePlanCompiler.ExpandForbiddenTraits(forbiddenTraits),
            Relationships: relationships,
            // Transient families are always evaluated after the persistent
            // dimensions and therefore are not part of the persistent order
            // list. The evaluator appends them as the final tie-break stage.
            DimensionOrder: persistentFamilies.Select(family => family.Id).ToArray(),
            Scenario: "runtime quality policy",
            Provenance: sources.Count > 0 ? "deluno-quality-policy+trash-guides" : "deluno-quality-policy",
            Sources: sources);
    }

    /// <summary>
    /// The default acquisition tie-break is explicit in the typed plan. It is
    /// deliberately bucketed rather than exposing or summing a seeder score.
    /// </summary>
    public static PreferenceFamily CreateSeederAvailabilityFamily(int order)
        => new(
            Id: "transient.seeders",
            Dimension: "Acquisition confidence · seeder availability",
            Order: order,
            Intent: PreferenceIntent.TieBreak,
            Levels:
            [
                new PreferenceFamilyLevel("available", 0, ["transient.seeders.available"]),
                new PreferenceFamilyLevel("none", 1, ["transient.seeders.none"])
            ],
            TargetLevelId: null,
            UpgradeDriving: false,
            Transient: true);

    private static IReadOnlyList<CustomFormatItem> SelectProfileFormats(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats)
    {
        var ids = Split(profile.CustomFormatIds);
        if (ids.Count == 0 || customFormats is null)
        {
            return [];
        }

        return customFormats
            .Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static bool IsForbiddenCategory(string? category)
        => string.Equals(category?.Trim(), "unwanted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category?.Trim(), "safety", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Split(string? value)
        => (value ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = string.Join(string.Empty, chars)
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}

public sealed record RuntimeQualityProfileCompilation(
    QualityProfileItem Profile,
    ReleasePreferencePlan Plan,
    IReadOnlyList<LegacyPreferenceRuleTranslation> AdvancedRules,
    IReadOnlyList<string> Warnings,
    bool RequiresReview);
