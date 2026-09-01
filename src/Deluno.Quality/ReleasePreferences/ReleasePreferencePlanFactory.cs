using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Creates the typed quality policy used by acquisition when a persisted
/// profile has not yet been compiled into a guide-backed plan. This keeps the
/// runtime path on the same contract while legacy custom-format rows remain
/// available for migration and Advanced diagnostics. Reviewed safety mappings
/// are compiled as forbidden hard gates; they never behave like a positive
/// preference.
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
        var version = $"profile/{profile.UpdatedUtc.ToUniversalTime():yyyyMMddHHmmssfffffff}/"
            + $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}";
        return CreateQualityPlan(
            profile.MediaType,
            profile.CutoffQuality,
            Split(profile.AllowedQualities),
            profile.UpgradeUntilCutoff,
            id: $"quality-profile/{profile.Id}",
            version: version,
            customFormats: selected,
            guidePackage: guidePackage);
    }

    /// <summary>
    /// Returns the typed plan plus the explicit Advanced review items that
    /// could not be given a reviewed semantic mapping. This is used by the
    /// preview API so legacy matcher rows are visible without allowing their
    /// numeric scores to affect acquisition.
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
        var guideFormats = guidePackage.CustomFormats
            .ToDictionary(item => item.TrashId, StringComparer.OrdinalIgnoreCase);
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

            if (!TryGetReviewedMapping(format, guideFormats, out _, out var explanation))
            {
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

    public static ReleasePreferencePlan CreateQualityPlan(
        string? mediaType,
        string? targetQuality,
        IReadOnlyList<string>? allowedQualities = null,
        bool upgradeUntilCutoff = true,
        string? id = null,
        string? version = null,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null)
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

        var qualityNames = normalizedAllowed.Length == 0
            ? MediaPolicyCatalog.Current.QualityRanks
                .OrderByDescending(item => item.Value)
                .Select(item => item.Key)
                .ToArray()
            : normalizedAllowed
                .Concat([cutoff])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(MediaPolicyCatalog.Current.GetRank)
                .ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();

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
        var upgradeDrivingTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<PreferencePlanProvenance>();
        foreach (var format in customFormats ?? [])
        {
            if (string.IsNullOrWhiteSpace(format.TrashId)
                || !guideFormats.TryGetValue(format.TrashId, out var guideFormat)
                || guideFormat.MappingStatus != GuideMappingStatus.Reviewed)
            {
                continue;
            }

            sources.Add(new PreferencePlanProvenance(
                SourceKind: guideFormat.SourceKind,
                SourceId: guideFormat.TrashId,
                SourceVersion: $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}",
                OriginalScore: guideFormat.OriginalScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssignedScore: format.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MappingId: $"{guidePackage.Id}:{guideFormat.TrashId}",
                MappingVersion: "trash-semantic-map/v1",
                Layer: "runtime-quality",
                MatcherDefinition: format.Conditions,
                MappedTraitIds: PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat.MappedTraitIds),
                MatcherAny: !string.IsNullOrWhiteSpace(format.TrashId)));

            foreach (var traitId in guideFormat.MappedTraitIds ?? [])
            {
                if (PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
                    && !trait.Transient)
                {
                    if (IsForbiddenCategory(guideFormat.Category))
                    {
                        forbiddenTraits.Add(trait.Id);
                        continue;
                    }

                    mappedTraits.Add(trait.Id);
                    if (format.UpgradeAllowed)
                    {
                        upgradeDrivingTraits.Add(trait.Id);
                    }
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

            var upgradeTarget = familyLevels
                .FirstOrDefault(level => level.TraitIds.Any(traitId => upgradeDrivingTraits.Contains(traitId)))
                ?.Id;
            var upgradeDriving = upgradeTarget is not null;

            mappedFamilies.Add(new PreferenceFamily(
                Id: $"guide.{Slug(group.Key)}",
                Dimension: group.Key,
                Order: mappedFamilies.Count + 2,
                Intent: upgradeDriving ? PreferenceIntent.Ranked : PreferenceIntent.TieBreak,
                Levels: familyLevels,
                TargetLevelId: upgradeTarget,
                UpgradeDriving: upgradeDriving,
                Transient: false));
        }

        var persistentFamilies = new[] { qualityFamily }.Concat(mappedFamilies).ToArray();
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

    private static bool TryGetReviewedMapping(
        CustomFormatItem format,
        IReadOnlyDictionary<string, GuideCustomFormat> guideFormats,
        out GuideCustomFormat? guideFormat,
        out string explanation)
    {
        if (string.IsNullOrWhiteSpace(format.TrashId)
            || !guideFormats.TryGetValue(format.TrashId, out guideFormat))
        {
            guideFormat = null;
            explanation = "The custom format has no reviewed TRaSH mapping. Its matcher and original score remain Advanced input; no numeric score affects Deluno decisions.";
            return false;
        }

        if (guideFormat.MappingStatus != GuideMappingStatus.Reviewed
            || guideFormat.MappedTraitIds is null
            || guideFormat.MappedTraitIds.Count == 0)
        {
            explanation = "This guide rule is not semantically reviewed. Its matcher and original score remain Advanced input; no numeric score affects Deluno decisions.";
            return false;
        }

        if (guideFormat.MappedTraitIds.Any(traitId =>
                !PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait) || trait.Transient))
        {
            explanation = "One or more mapped traits are not available in the current typed registry, so the original matcher remains Advanced input.";
            return false;
        }

        explanation = string.Empty;
        return true;
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
