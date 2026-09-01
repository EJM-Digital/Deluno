using System.Globalization;
using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Guides;

/// <summary>
/// Compiles one reviewed guide profile into the shared typed preference model.
/// The compiler is explicit about the boundary: a reviewed preference mapping
/// becomes a typed, non-additive family, while a reviewed safety mapping
/// becomes a forbidden hard gate. An advanced mapping remains an auditable
/// legacy rule. A guide score is retained as provenance, but it is never used
/// to order or upgrade a release.
/// </summary>
public static class GuidePlanCompiler
{
    public const string MappingVersion = "trash-semantic-map/v1";

    private static readonly IReadOnlyDictionary<string, string[]> PreferredTraitOrder =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] =
            [
                "source.remux", "source.bluray", "source.br-disk", "source.webdl",
                "source.webrip", "source.hdtv", "source.dvd", "source.cam"
            ],
            ["audio.format"] =
            [
                "audio.format.truehd-atmos", "audio.format.dtsx", "audio.format.truehd",
                "audio.format.dts-hd-ma", "audio.format.eac3-atmos", "audio.format.eac3",
                "audio.format.dts", "audio.format.flac", "audio.format.pcm",
                "audio.format.aac", "audio.format.opus", "audio.format.mp3"
            ],
            ["video.dynamic-range"] =
            [
                "video.dynamic-range.dolby-vision-fallback", "video.dynamic-range.dolby-vision",
                "video.dynamic-range.hdr10-plus", "video.dynamic-range.hdr10",
                "video.dynamic-range.hlg", "video.dynamic-range.sdr"
            ],
            ["video.codec"] =
            ["video.codec.av1", "video.codec.hevc", "video.codec.h264", "video.codec.vp9", "video.codec.xvid", "video.codec.divx", "video.codec.mpeg-2"],
            ["audio.channels"] =
            ["audio.channels.9-1", "audio.channels.7-1", "audio.channels.5-1", "audio.channels.2-0", "audio.channels.1-0"],
            ["release.revision"] =
            ["release.revision.repack3", "release.revision.repack2", "release.revision.proper"]
        };

    /// <summary>
    /// Orders typed trait levels using Deluno's reviewed best-first semantics.
    /// Runtime plans and guide-compiled plans must use the same ordering or a
    /// profile can produce different decisions depending on which entry point
    /// built its plan.
    /// </summary>
    public static IReadOnlyList<PreferenceFamilyLevel> OrderTraitLevels(
        string dimension,
        IEnumerable<string> traitIds,
        IReadOnlyList<string>? sourceOrder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentNullException.ThrowIfNull(traitIds);

        var preferred = dimension.Equals("source", StringComparison.OrdinalIgnoreCase)
            ? sourceOrder is { Count: > 0 } ? sourceOrder : PreferredTraitOrder["source"]
            : PreferredTraitOrder.GetValueOrDefault(dimension, []);
        var ranks = preferred
            .Select((traitId, index) => (traitId, index))
            .ToDictionary(item => item.traitId, item => item.index, StringComparer.OrdinalIgnoreCase);

        return traitIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(traitId => ranks.GetValueOrDefault(traitId, int.MaxValue))
            .ThenBy(traitId => traitId, StringComparer.OrdinalIgnoreCase)
            .Select((traitId, index) => new PreferenceFamilyLevel(
                Id: Slug(traitId),
                Rank: index,
                TraitIds: [traitId]))
            .ToArray();
    }

    public static GuideProfileCompilation Compile(
        string profileId,
        string? mediaType = null,
        GuidePackage? package = null)
    {
        package ??= GuidePackageCatalog.Current;
        var profile = package.QualityProfiles.FirstOrDefault(item =>
            string.Equals(item.Id, profileId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            throw new KeyNotFoundException($"Guide quality profile '{profileId}' was not found in package '{package.Id}'.");
        }

        var normalizedMediaType = NormalizeMediaType(mediaType ?? profile.MediaType);
        var warnings = new List<string>();
        var advanced = new List<LegacyPreferenceRuleTranslation>();
        var sources = new List<PreferencePlanProvenance>
        {
            new(
                SourceKind: "trash-guide-package",
                SourceId: package.Id,
                SourceVersion: $"{package.Version}:{package.Source.UpstreamRevision}",
                MappingId: package.Id,
                MappingVersion: MappingVersion,
                Layer: "guide-default")
        };

        var tiersById = package.QualityTiers.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var qualityLevels = new List<PreferenceFamilyLevel>();
        var seenQuality = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forbiddenTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tierId in profile.QualityOrder ?? [])
        {
            if (!tiersById.TryGetValue(tierId, out var tier))
            {
                warnings.Add($"Guide profile '{profile.Id}' refers to missing quality tier '{tierId}'.");
                continue;
            }

            var quality = NormalizeGuideQuality(tier.Label);
            if (string.IsNullOrWhiteSpace(quality))
            {
                warnings.Add($"Guide quality tier '{tier.Id}' could not be mapped to Deluno's quality vocabulary.");
                continue;
            }

            var qualityTraitId = InstalledPreferenceEvaluationFactory.QualityTraitId(quality);
            if (!PreferenceTraitRegistry.Current.TryResolve(qualityTraitId, out var qualityTrait))
            {
                warnings.Add($"Guide quality tier '{tier.Id}' maps to unknown typed trait '{qualityTraitId}'.");
                continue;
            }

            if (seenQuality.Add(qualityTrait.NormalizedId))
            {
                qualityLevels.Add(new PreferenceFamilyLevel(
                    Id: Slug(quality),
                    Rank: qualityLevels.Count,
                    TraitIds: [qualityTrait.Id]));
            }
        }

        var cutoff = tiersById.TryGetValue(profile.CutoffQualityId, out var cutoffTier)
            ? NormalizeGuideQuality(cutoffTier.Label)
            : null;
        if (string.IsNullOrWhiteSpace(cutoff))
        {
            warnings.Add($"Guide profile '{profile.Id}' has no usable cutoff quality.");
        }
        else if (seenQuality.Add(InstalledPreferenceEvaluationFactory.QualityTraitId(cutoff)))
        {
            qualityLevels.Add(new PreferenceFamilyLevel(
                Id: Slug(cutoff),
                Rank: qualityLevels.Count,
                TraitIds: [InstalledPreferenceEvaluationFactory.QualityTraitId(cutoff)]));
            warnings.Add($"Cutoff '{cutoff}' was not listed in the guide order and was appended as the explicit stop-when target.");
        }

        if (qualityLevels.Count == 0)
        {
            throw new InvalidOperationException($"Guide profile '{profile.Id}' produced no usable quality levels.");
        }

        var familiesByDimension = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var sourceOrder = new List<string>();
        foreach (var tierId in profile.QualityOrder ?? [])
        {
            if (!tiersById.TryGetValue(tierId, out var tier)
                || !PreferenceTraitRegistry.Current.TryResolveObserved("source", tier.Source, out var sourceTrait))
            {
                continue;
            }

            AddTrait(familiesByDimension, "source", sourceTrait.Id);
            if (!sourceOrder.Contains(sourceTrait.Id, StringComparer.OrdinalIgnoreCase))
            {
                sourceOrder.Add(sourceTrait.Id);
            }
        }

        var formatsById = package.CustomFormats.ToDictionary(item => item.TrashId, StringComparer.OrdinalIgnoreCase);
        var mappedTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recommendation in profile.RecommendedFormats ?? [])
        {
            if (!formatsById.TryGetValue(recommendation.TrashId, out var format))
            {
                warnings.Add($"Guide profile '{profile.Id}' refers to missing custom format '{recommendation.TrashId}'.");
                advanced.Add(AdvancedRule(profile, recommendation.TrashId, recommendation.Score, null,
                    "The guide recommendation has no matching package format and remains review-only."));
                continue;
            }

            sources.Add(new PreferencePlanProvenance(
                SourceKind: format.SourceKind,
                SourceId: format.TrashId,
                SourceVersion: package.Source.UpstreamRevision,
                OriginalScore: format.OriginalScore.ToString(CultureInfo.InvariantCulture),
                AssignedScore: recommendation.Score.ToString(CultureInfo.InvariantCulture),
                MappingId: $"{package.Id}:{format.TrashId}",
                MappingVersion: MappingVersion,
                Layer: "guide-default",
                MatcherDefinition: JsonSerializer.Serialize(format.Patterns ?? []),
                MappedTraitIds: PreferenceTraitRegistry.Current.CanonicalizeIds(format.MappedTraitIds),
                MatcherAny: true));

            if (format.MappingStatus != GuideMappingStatus.Reviewed || format.MappedTraitIds is null || format.MappedTraitIds.Count == 0)
            {
                advanced.Add(AdvancedRule(profile, format.TrashId, format.OriginalScore, format,
                    "This guide rule is not semantically reviewed. Its matcher and original score are retained as Advanced input; no numeric score affects Deluno decisions."));
                continue;
            }

            var formatHasUnknownMapping = false;
            foreach (var traitId in format.MappedTraitIds)
            {
                if (!PreferenceTraitRegistry.Current.TryResolve(traitId, out var definition)
                    || definition.Transient)
                {
                    formatHasUnknownMapping = true;
                    warnings.Add($"Guide custom format '{format.TrashId}' has no usable reviewed mapping for '{traitId}'.");
                    continue;
                }

                if (IsForbiddenCategory(format.Category))
                {
                    // A reviewed safety mapping is an explicit hard gate. It
                    // must not be reduced to an explanatory/tie-break family,
                    // because that would allow an unwanted release through.
                    forbiddenTraits.Add(definition.Id);
                    continue;
                }

                mappedTraits.Add(definition.Id);
                AddTrait(familiesByDimension, definition.Dimension, definition.Id);
            }

            if (formatHasUnknownMapping)
            {
                advanced.Add(AdvancedRule(profile, format.TrashId, format.OriginalScore, format,
                    "One or more mapped traits were not in the current typed registry, so the original matcher remains review-only."));
            }
        }

        // Include the target side of known implications so a specific observed
        // trait can satisfy its broader capability without being counted twice.
        var relationships = PreferenceTraitRegistry.Current.Relationships
            .Where(relationship => mappedTraits.Contains(relationship.FromTraitId)
                && PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out _))
            .ToArray();
        foreach (var relationship in relationships)
        {
            if (PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out var target))
            {
                AddTrait(familiesByDimension, target.Dimension, target.Id);
            }
        }

        var families = new List<PreferenceFamily>
        {
            new(
                Id: "quality",
                Dimension: "Quality",
                Order: 1,
                Intent: PreferenceIntent.Ranked,
                Levels: qualityLevels,
                TargetLevelId: string.IsNullOrWhiteSpace(cutoff) ? null : Slug(cutoff),
                UpgradeDriving: profile.UpgradeAllowed && !string.IsNullOrWhiteSpace(cutoff),
                Transient: false)
        };

        foreach (var dimension in familiesByDimension.Keys
                     .OrderBy(item => item.Equals("source", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            var levels = OrderTraitLevels(dimension, familiesByDimension[dimension], sourceOrder);
            if (levels.Count == 0)
            {
                continue;
            }

            families.Add(new PreferenceFamily(
                Id: $"guide.{Slug(dimension)}",
                Dimension: DisplayDimension(dimension),
                Order: families.Count + 1,
                Intent: PreferenceIntent.TieBreak,
                Levels: levels,
                TargetLevelId: null,
                UpgradeDriving: false,
                Transient: false));
        }

        // Keep guide-backed plans on the same explicit candidate-selection
        // contract as runtime quality plans. Seeder availability is a final
        // transient tie-break only and is excluded from DimensionOrder.
        families.Add(ReleasePreferencePlanFactory.CreateSeederAvailabilityFamily(families.Count + 1));

        var plan = new ReleasePreferencePlan(
            Id: $"guide/{package.Id}/{profile.Id}",
            Version: $"{package.Version}:{package.Source.UpstreamRevision}",
            MediaType: normalizedMediaType,
            Families: families,
            ForbiddenTraitIds: ExpandForbiddenTraits(forbiddenTraits),
            Relationships: relationships,
            DimensionOrder: families.Where(family => !family.Transient).Select(family => family.Id).ToArray(),
            Scenario: profile.Name,
            Provenance: $"{package.Source.SourceName}/{package.Source.UpstreamRevision}",
            Sources: sources);

        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        return new GuideProfileCompilation(
            package,
            profile,
            plan,
            advanced,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            RequiresReview: advanced.Count > 0 || warnings.Count > 0);
    }

    private static LegacyPreferenceRuleTranslation AdvancedRule(
        GuideQualityProfile profile,
        string ruleId,
        int originalScore,
        GuideCustomFormat? format,
        string explanation)
        => new(
            RuleId: ruleId,
            Name: format?.Name ?? ruleId,
            TrashId: format?.TrashId,
            MediaType: NormalizeMediaType(profile.MediaType),
            OriginalScore: format?.OriginalScore ?? originalScore,
            UpgradeAllowed: profile.UpgradeAllowed,
            Conditions: format is null ? string.Empty : string.Join(Environment.NewLine, format.Patterns.Select(pattern => $"regex: {pattern}")),
            Kind: format is null ? LegacyPreferenceRuleKind.Invalid : LegacyPreferenceRuleKind.UnmappedAdvanced,
            ProposedIntent: null,
            RequiresReview: true,
            Explanation: explanation);

    private static void AddTrait(
        IDictionary<string, HashSet<string>> familiesByDimension,
        string dimension,
        string traitId)
    {
        if (!familiesByDimension.TryGetValue(dimension, out var traits))
        {
            traits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            familiesByDimension[dimension] = traits;
        }

        traits.Add(traitId);
    }

    private static bool IsForbiddenCategory(string? category)
        => string.Equals(category?.Trim(), "unwanted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category?.Trim(), "safety", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A forbidden broad capability also forbids a more specific capability
    /// that explicitly implies, carries, or subsumes it. Without this closure,
    /// an unwanted TrueHD rule could still admit TrueHD Atmos because the
    /// release only advertises the more specific trait.
    /// </summary>
    public static IReadOnlyList<string> ExpandForbiddenTraits(IEnumerable<string> traitIds)
    {
        var forbidden = new HashSet<string>(
            (traitIds ?? [])
                .Where(traitId => !string.IsNullOrWhiteSpace(traitId))
                .Select(traitId => traitId.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var relationship in PreferenceTraitRegistry.Current.Relationships.Where(relationship =>
                         relationship.Kind is PreferenceRelationshipKind.Implies
                             or PreferenceRelationshipKind.Subsumes
                             or PreferenceRelationshipKind.CoreOf
                             or PreferenceRelationshipKind.CarriedBy))
            {
                if (forbidden.Contains(relationship.ToTraitId.Trim())
                    && forbidden.Add(relationship.FromTraitId.Trim().ToLowerInvariant()))
                {
                    changed = true;
                }
            }
        }

        return forbidden.OrderBy(traitId => traitId, StringComparer.Ordinal).ToArray();
    }

    private static string DisplayDimension(string dimension)
        => dimension
            .Replace('.', ' ')
            .Replace('-', ' ')
            .Trim() switch
        {
            "source" => "Source",
            "audio format" => "Audio format",
            "audio channels" => "Audio channels",
            "video dynamic range" => "HDR / dynamic range",
            "video codec" => "Video codec",
            "release revision" => "Release revision",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dimension.Replace('.', ' ').Replace('-', ' '))
        };

    private static string? NormalizeGuideQuality(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        // TRaSH uses both WEB-DL/WEBRip and 4K labels while Deluno's quality
        // ladder uses WEB and 2160p. This is a vocabulary alias, not a change
        // to the guide's ordering or semantics.
        var normalized = label.Trim()
            .Replace("web-dl", "web", StringComparison.OrdinalIgnoreCase)
            .Replace("webrip", "web", StringComparison.OrdinalIgnoreCase)
            .Replace("4k", "2160p", StringComparison.OrdinalIgnoreCase);
        return MediaPolicyCatalog.Current.NormalizeQuality(normalized);
    }

    private static string NormalizeMediaType(string? value)
        => string.Equals(value, "anime", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : MediaPolicyCatalog.NormalizeMediaType(value);

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

public sealed record GuideProfileCompilation(
    GuidePackage Package,
    GuideQualityProfile Profile,
    ReleasePreferencePlan Plan,
    IReadOnlyList<LegacyPreferenceRuleTranslation> AdvancedRules,
    IReadOnlyList<string> Warnings,
    bool RequiresReview)
{
    /// <summary>
    /// Exposes the canonical compiled-plan hash at the compilation boundary.
    /// The plan itself intentionally keeps this derived value out of its JSON
    /// shape, so callers can verify the exact typed plan without reimplementing
    /// the hashing algorithm.
    /// </summary>
    public string PlanHash => Plan.PlanHash;
}
