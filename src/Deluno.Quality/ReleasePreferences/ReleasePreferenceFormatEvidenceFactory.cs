using System.Text.Json;
using System.Text.RegularExpressions;
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
        GuidePackage? package = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(filePath) || customFormats is not { Count: > 0 })
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

        var evidence = new List<ReleasePreferenceFormatEvidence>();
        foreach (var customFormat in customFormats)
        {
            var source = planSources.FirstOrDefault(candidate => SourceMatches(candidate, customFormat));
            if (source is null)
            {
                continue;
            }

            var matched = false;
            IReadOnlyList<string> mappedTraitIds = PreferenceTraitRegistry.Current.CanonicalizeIds(source.MappedTraitIds);
            if (!string.IsNullOrWhiteSpace(source.MatcherDefinition))
            {
                // New plans carry the matcher snapshot with the source. It
                // is intentionally preferred to the current guide package:
                // changing a guide or editing a custom-format row must not
                // mutate the meaning of an already-persisted plan.
                matched = MatchesDefinition(filePath, source.MatcherDefinition, source.MatcherAny);
            }
            else if (!string.IsNullOrWhiteSpace(customFormat.TrashId)
                && formatsByTrashId.TryGetValue(customFormat.TrashId, out var guideFormat)
                && guideFormat.MappingStatus == GuideMappingStatus.Reviewed)
            {
                // Backward-compatible read path for plans written before the
                // matcher snapshot was added to provenance. New writes never
                // rely on this mutable fallback.
                matched = MatchesAnyPattern(filePath, guideFormat.Patterns);
                mappedTraitIds = PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat.MappedTraitIds);
            }

            if (!matched)
            {
                continue;
            }

            var traitIds = mappedTraitIds
                .Where(traitId => planTraits.Contains(traitId)
                    && PreferenceTraitRegistry.Current.TryResolve(traitId, out var definition)
                    && !definition.Transient)
                .Select(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var definition)
                    ? definition.Id
                    : traitId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(traitId => traitId, StringComparer.Ordinal)
                .ToArray();

            evidence.Add(new ReleasePreferenceFormatEvidence(
                customFormat.Id.Trim(),
                source.SourceId.Trim(),
                traitIds));
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

    private static bool MatchesDefinition(string value, string definition, bool any)
    {
        var trimmed = definition.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var elements = document.RootElement.EnumerateArray().ToArray();
                if (elements.Length == 0)
                {
                    return false;
                }

                if (elements.All(element => element.ValueKind == JsonValueKind.String))
                {
                    return any
                        ? elements.Any(element => MatchesPattern(value, element.GetString()))
                        : elements.All(element => MatchesPattern(value, element.GetString()));
                }

                var conditions = elements
                    .Where(element => element.ValueKind == JsonValueKind.Object)
                    .Select(ReadCondition)
                    .Where(condition => condition is not null)
                    .Cast<ImmutableMatcherCondition>()
                    .ToArray();
                if (conditions.Length == 0)
                {
                    return false;
                }

                var results = conditions
                    .Select(condition => condition with
                    {
                        Matched = ApplyNegation(
                            MatchesCondition(value, condition.Type, condition.Value),
                            condition.Negate)
                    })
                    .ToArray();
                var required = results.Where(condition => condition.Required).ToArray();
                return any
                    ? (required.Length == 0 ? results : required).Any(condition => condition.Matched)
                    : required.Length > 0
                        ? required.All(condition => condition.Matched)
                        : results.Any(condition => condition.Matched);
            }
        }
        catch (JsonException)
        {
            // Legacy conditions are line-oriented and are handled below.
        }

        var lines = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)
                ? line["regex:".Length..].Trim()
                : line)
            .Where(line => line.Length > 0)
            .ToArray();
        return any
            ? lines.Any(line => MatchesPattern(value, line))
            : lines.All(line => MatchesPattern(value, line));
    }

    private static ImmutableMatcherCondition? ReadCondition(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        var conditionValue = element.TryGetProperty("value", out var valueElement)
            ? valueElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(conditionValue))
        {
            return null;
        }

        var negate = element.TryGetProperty("negate", out var negateElement)
            && negateElement.ValueKind == JsonValueKind.True;
        var required = !element.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.False;
        return new ImmutableMatcherCondition(type ?? "releaseTitle", conditionValue, negate, required, false);
    }

    private static bool MatchesCondition(string value, string type, string pattern)
    {
        var lower = value.ToLowerInvariant();
        var normalizedType = type.Trim().ToLowerInvariant();
        var normalizedPattern = pattern.Trim().ToLowerInvariant();
        return normalizedType switch
        {
            "releasetitle" or "regex" => MatchesPattern(value, pattern),
            "source" => normalizedPattern switch
            {
                "bluray" => lower.Contains("bluray", StringComparison.Ordinal) || lower.Contains("blu-ray", StringComparison.Ordinal),
                "web" => lower.Contains("web-dl", StringComparison.Ordinal) || lower.Contains("webdl", StringComparison.Ordinal),
                "webrip" => lower.Contains("webrip", StringComparison.Ordinal),
                "remux" => lower.Contains("remux", StringComparison.Ordinal),
                "hdtv" => lower.Contains("hdtv", StringComparison.Ordinal),
                _ => lower.Contains(normalizedPattern, StringComparison.Ordinal)
            },
            "resolution" => lower.Contains(normalizedPattern.Replace("p", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal),
            "hdr" => normalizedPattern switch
            {
                "dv" or "dolbyvision" => lower.Contains("dolby.vision", StringComparison.Ordinal) || lower.Contains("dovi", StringComparison.Ordinal) || lower.Contains(".dv.", StringComparison.Ordinal),
                "hdr10plus" or "hdr10+" => lower.Contains("hdr10+", StringComparison.Ordinal) || lower.Contains("hdr10plus", StringComparison.Ordinal),
                _ => lower.Contains(normalizedPattern, StringComparison.Ordinal)
            },
            "codec" => normalizedPattern switch
            {
                "x265" or "hevc" => lower.Contains("x265", StringComparison.Ordinal) || lower.Contains("h265", StringComparison.Ordinal) || lower.Contains("hevc", StringComparison.Ordinal),
                "x264" or "avc" => lower.Contains("x264", StringComparison.Ordinal) || lower.Contains("h264", StringComparison.Ordinal) || lower.Contains("avc", StringComparison.Ordinal),
                _ => lower.Contains(normalizedPattern, StringComparison.Ordinal)
            },
            "releasegroup" => Regex.Match(value, @"-(?<group>[A-Za-z0-9]{2,20})$", RegexOptions.CultureInvariant).Groups["group"].Value.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => lower.Contains(normalizedPattern, StringComparison.Ordinal)
        };
    }

    private static bool MatchesPattern(string value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(
                value,
                pattern.Trim(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return value.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool ApplyNegation(bool value, bool negate) => negate ? !value : value;

    private sealed record ImmutableMatcherCondition(
        string Type,
        string Value,
        bool Negate,
        bool Required,
        bool Matched);

    private static bool MatchesAnyPattern(string value, IEnumerable<string>? patterns)
    {
        foreach (var pattern in patterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                if (Regex.IsMatch(
                    value,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Package validation rejects invalid patterns. Keeping this
                // defensive guard makes a persisted/externally supplied
                // package unable to break import or snapshot creation.
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern is evidence we cannot safely use;
                // it remains absent rather than becoming an inferred match.
            }
        }

        return false;
    }
}

public sealed record ReleasePreferenceFormatEvidence(
    string RuleId,
    string SourceId,
    IReadOnlyList<string> TraitIds);
