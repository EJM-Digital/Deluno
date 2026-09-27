using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Carries guide-backed matcher evidence into an installed-file snapshot.
/// Matching is deliberately limited to the custom formats selected by the
/// profile's immutable plan; an inventory-wide format must not silently alter
/// a file's effective preference state.
/// </summary>
public static class ReleasePreferenceFormatEvidenceFactory
{
    public static IReadOnlyList<ReleasePreferenceFormatEvidence> Match(
        ReleasePreferencePlan plan,
        string filePath,
        IReadOnlyList<CustomFormatItem>? customFormats,
        GuidePackage? package = null,
        string? indexerFlags = null,
        long? releaseSizeBytes = null)
        => Evaluate(plan, filePath, customFormats, package, indexerFlags, releaseSizeBytes)
            .Where(item => item.IsMatch)
            .ToArray();

    /// <summary>
    /// Evaluates the immutable matchers captured in a plan. Plan-scoped custom
    /// rules emit an explicit matched/not-matched state, so a clean rename and
    /// an open-world filename parser cannot turn a known rule result into
    /// unknown evidence later.
    /// </summary>
    public static IReadOnlyList<ReleasePreferenceFormatEvidence> Evaluate(
        ReleasePreferencePlan plan,
        string filePath,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? package = null,
        // The flags the indexer reported for this exact release (#745). Null
        // for an installed-file re-evaluation, where there is no indexer left
        // to ask; a fresh search candidate carries its own.
        string? indexerFlags = null,
        // The release's total size (#775), for a "size" condition. The
        // caller decides what "size" means for its evidence: a fresh search
        // candidate's reported size, or an installed file's size on disk.
        long? releaseSizeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        var planSources = (plan.Sources ?? [])
            .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.SourceId))
            .ToArray();
        if (planSources.Length == 0)
        {
            return [];
        }

        var planTraits = (plan.Families ?? [])
            .SelectMany(family => family.Levels ?? [])
            .SelectMany(level => level.TraitIds ?? [])
            .Concat(plan.RequiredTraitIds ?? [])
            .Concat((plan.RequiredAnyTraitGroups ?? [])
                .Where(group => group is not null)
                .SelectMany(group => group))
            .Concat(plan.ForbiddenTraitIds ?? [])
            .Where(traitId => !string.IsNullOrWhiteSpace(traitId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var formatsByTrashId = (package?.CustomFormats ?? [])
            .Where(format => !string.IsNullOrWhiteSpace(format.TrashId))
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var formatInventory = customFormats ?? [];

        var evidence = new List<ReleasePreferenceFormatEvidence>();
        foreach (var source in planSources)
        {
            var customFormat = formatInventory.FirstOrDefault(format =>
                (!string.IsNullOrWhiteSpace(source.RuleId)
                    && string.Equals(source.RuleId, format.Id, StringComparison.OrdinalIgnoreCase))
                || SourceMatches(source, format));

            var matched = false;
            var matcherAvailable = false;
            IReadOnlyList<string> mappedTraitIds = PreferenceTraitRegistry.Current.CanonicalizeIds(source.MappedTraitIds);
            if (!string.IsNullOrWhiteSpace(source.MatcherDefinition))
            {
                // New plans carry the matcher snapshot with the source. It
                // is intentionally preferred to the current guide package:
                // changing a guide or editing a custom-format row must not
                // mutate the meaning of an already-persisted plan.
                matched = CustomFormatDefinitionMatcher.Evaluate(
                    filePath,
                    source.MatcherDefinition,
                    source.MatcherAny,
                    plan.MediaType,
                    indexerFlags,
                    releaseSizeBytes).IsMatch;
                matcherAvailable = true;
            }
            else if (!string.IsNullOrWhiteSpace(customFormat?.TrashId)
                && formatsByTrashId.TryGetValue(customFormat.TrashId, out var guideFormat)
                && guideFormat.MappingStatus == GuideMappingStatus.Reviewed)
            {
                // Backward-compatible read path for plans written before the
                // matcher snapshot was added to provenance. New writes never
                // rely on this mutable fallback.
                matched = CustomFormatDefinitionMatcher.Evaluate(
                    filePath,
                    GuideFormatConditions.For(guideFormat),
                    anyCondition: !GuideFormatConditions.IsGenuine(guideFormat),
                    plan.MediaType,
                    indexerFlags,
                    releaseSizeBytes).IsMatch;
                mappedTraitIds = PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat.MappedTraitIds);
                matcherAvailable = true;
            }

            if (!matcherAvailable)
            {
                continue;
            }

            var eligibleTraitIds = mappedTraitIds
                .Where(traitId => planTraits.Contains(traitId))
                .Where(traitId => CustomFormatPreferenceTraits.IsDynamic(traitId)
                    || (PreferenceTraitRegistry.Current.TryResolve(traitId, out var definition) && !definition.Transient))
                .Select(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var definition)
                    ? definition.Id
                    : traitId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(traitId => traitId, StringComparer.Ordinal)
                .ToArray();
            var presentTraits = eligibleTraitIds
                .Where(traitId => CustomFormatPreferenceTraits.IsDynamic(traitId)
                    ? matched
                        ? traitId.EndsWith(".matched", StringComparison.Ordinal)
                        : traitId.EndsWith(".not-matched", StringComparison.Ordinal)
                    : matched)
                .ToArray();
            var absentTraits = eligibleTraitIds
                .Where(CustomFormatPreferenceTraits.IsDynamic)
                .Except(presentTraits, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ruleId = !string.IsNullOrWhiteSpace(source.RuleId)
                ? source.RuleId.Trim()
                : customFormat?.Id.Trim() ?? source.SourceId.Trim();

            evidence.Add(new ReleasePreferenceFormatEvidence(
                ruleId,
                source.SourceId.Trim(),
                presentTraits,
                matched,
                absentTraits,
                source.RuleName ?? customFormat?.Name));
        }

        return evidence
            .GroupBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool SourceMatches(
        PreferencePlanProvenance source,
        CustomFormatItem customFormat)
    {
        if (string.Equals(source.SourceId?.Trim(), customFormat.TrashId?.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.SourceId?.Trim(), customFormat.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mappingId = source.MappingId?.Trim();
        return !string.IsNullOrWhiteSpace(mappingId)
            && !string.IsNullOrWhiteSpace(customFormat.TrashId)
            && mappingId.EndsWith($":{customFormat.TrashId.Trim()}", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed record ReleasePreferenceFormatEvidence(
    string RuleId,
    string SourceId,
    IReadOnlyList<string> TraitIds,
    bool IsMatch = true,
    IReadOnlyList<string>? AbsentTraitIds = null,
    string? RuleName = null);
