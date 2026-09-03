using System.Text.Json;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Canonical JSON for an immutable release-preference plan. The plan hash is
/// the identity used by decisions; this codec keeps the stored representation
/// stable when callers supplied equivalent input in a different collection
/// order.
/// </summary>
public static class ReleasePreferencePlanCodec
{
    private static JsonSerializerOptions JsonOptions => ReleasePreferenceJson.Options;

    public static string Serialize(ReleasePreferencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        return JsonSerializer.Serialize(Canonicalize(plan), JsonOptions);
    }

    public static ReleasePreferencePlan Deserialize(string json)
    {
        var plan = JsonSerializer.Deserialize<ReleasePreferencePlan>(json, JsonOptions)
                   ?? throw new JsonException("The release-preference plan was empty.");
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        return plan;
    }

    private static ReleasePreferencePlan Canonicalize(ReleasePreferencePlan plan)
        => plan with
        {
            Families = plan.OrderedFamilies
                .Select(family => family with
                {
                    Levels = family.OrderedLevels
                        .Select(level => level with { TraitIds = level.NormalizedTraitIds })
                        .ToArray()
                })
                .ToArray(),
            RequiredTraitIds = (plan.RequiredTraitIds ?? [])
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .Select(Normalize)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(trait => trait, StringComparer.Ordinal)
                .ToArray(),
            ForbiddenTraitIds = (plan.ForbiddenTraitIds ?? [])
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .Select(Normalize)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(trait => trait, StringComparer.Ordinal)
                .ToArray(),
            RequiredAnyTraitGroups = (plan.RequiredAnyTraitGroups ?? [])
                .Where(group => group is { Count: > 0 })
                .Select(group => (IReadOnlyList<string>)group
                    .Where(trait => !string.IsNullOrWhiteSpace(trait))
                    .Select(Normalize)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(trait => trait, StringComparer.Ordinal)
                    .ToArray())
                .Where(group => group.Count > 0)
                .OrderBy(group => string.Join("|", group), StringComparer.Ordinal)
                .ToArray(),
            Relationships = (plan.Relationships ?? [])
                .Select(relationship => new PreferenceRelationship(
                    Normalize(relationship.FromTraitId),
                    Normalize(relationship.ToTraitId),
                    relationship.Kind))
                .OrderBy(relationship => relationship.FromTraitId, StringComparer.Ordinal)
                .ThenBy(relationship => relationship.ToTraitId, StringComparer.Ordinal)
                .ThenBy(relationship => relationship.Kind)
                .ToArray(),
            DimensionOrder = plan.DimensionOrder?.Select(Normalize).ToArray(),
            Overrides = (plan.Overrides ?? new Dictionary<string, string>())
                .OrderBy(item => Normalize(item.Key), StringComparer.Ordinal)
                .ToDictionary(item => Normalize(item.Key), item => item.Value?.Trim() ?? string.Empty, StringComparer.Ordinal),
            Sources = (plan.Sources ?? [])
                .Select(source => source with
                {
                    SourceKind = Normalize(source.SourceKind),
                    SourceId = Normalize(source.SourceId),
                    SourceVersion = Normalize(source.SourceVersion),
                    OriginalScore = NormalizeNullable(source.OriginalScore),
                    AssignedScore = NormalizeNullable(source.AssignedScore),
                    MappingId = NormalizeNullable(source.MappingId),
                    MappingVersion = NormalizeNullable(source.MappingVersion),
                    Layer = NormalizeNullable(source.Layer),
                    MatcherDefinition = NormalizeNullable(source.MatcherDefinition),
                    MappedTraitIds = PreferenceTraitRegistry.Current.CanonicalizeIds(source.MappedTraitIds)
                })
                .OrderBy(source => Normalize(source.SourceKind), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.SourceId), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.SourceVersion), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.MappingId), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.MappingVersion), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.Layer), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.MatcherDefinition), StringComparer.Ordinal)
                .ThenBy(source => source.MatcherAny)
                .ThenBy(source => Normalize(source.OriginalScore), StringComparer.Ordinal)
                .ThenBy(source => Normalize(source.AssignedScore), StringComparer.Ordinal)
                .ThenBy(source => string.Join("|", source.MappedTraitIds ?? []), StringComparer.Ordinal)
                .ToArray()
        };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
