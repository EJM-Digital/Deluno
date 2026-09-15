using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// The one custom-format matcher used by dry-run, acquisition, import and
/// later installed-file re-evaluation. Keeping the truth here prevents a UI
/// preview from accepting a rule that the typed decision path interprets
/// differently.
/// </summary>
public static partial class CustomFormatDefinitionMatcher
{
    private static readonly IReadOnlySet<string> SupportedTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "releaseTitle",
            "regex",
            "source",
            "resolution",
            "hdr",
            "codec",
            "releaseGroup",
            "language"
        };

    /// <summary>
    /// True only when every condition can be interpreted by this matcher.
    /// Partial interpretation is unsafe: silently dropping one required or
    /// negated clause changes the rule.
    /// </summary>
    public static bool CanEvaluate(string? rawConditions)
    {
        var conditions = ParseConditions(rawConditions);
        return conditions.Count > 0
               && conditions.All(condition =>
                   !string.IsNullOrWhiteSpace(condition.Value)
                   && SupportedTypes.Contains(condition.Type));
    }

    public static CustomFormatDefinitionMatch Evaluate(
        string releaseName,
        string? rawConditions,
        bool anyCondition)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return new CustomFormatDefinitionMatch(false, [], ["Release name is empty"]);
        }

        if (string.IsNullOrWhiteSpace(rawConditions))
        {
            return new CustomFormatDefinitionMatch(false, [], ["No conditions defined"]);
        }

        var conditions = ParseConditions(rawConditions);
        if (conditions.Count == 0
            || conditions.Any(condition =>
                string.IsNullOrWhiteSpace(condition.Value)
                || !SupportedTypes.Contains(condition.Type)))
        {
            return new CustomFormatDefinitionMatch(false, [], ["No complete supported condition definition"]);
        }

        var lower = releaseName.ToLowerInvariant();
        var inferredGroup = InferReleaseGroup(releaseName);
        var matched = new List<string>();
        var missed = new List<string>();
        var evaluations = new List<(CustomFormatDefinitionCondition Condition, bool IsMatch)>();
        var requiredCount = conditions.Count(condition => condition.Required);
        var requiredMatchedCount = 0;

        foreach (var condition in conditions)
        {
            var hit = EvaluateSingleCondition(releaseName, lower, inferredGroup, condition);
            var effectiveHit = condition.Negate ? !hit : hit;
            var label = $"{condition.Type}:{condition.Value}";
            if (condition.Negate)
            {
                label = "NOT " + label;
            }

            if (effectiveHit)
            {
                matched.Add(label);
                if (condition.Required)
                {
                    requiredMatchedCount++;
                }
            }
            else
            {
                missed.Add(label);
            }

            evaluations.Add((condition, effectiveHit));
        }

        var isMatch = conditions.All(condition => condition.ExternalGroup is not null)
            ? evaluations
                .GroupBy(evaluation => evaluation.Condition.ExternalGroup, StringComparer.OrdinalIgnoreCase)
                .All(group =>
                    !group.Any(evaluation => evaluation.Condition.Required && !evaluation.IsMatch)
                    && group.Any(evaluation => evaluation.IsMatch))
            : anyCondition
                ? requiredCount > 0
                    ? requiredMatchedCount > 0
                    : matched.Count > 0
                : requiredCount > 0
                    ? requiredMatchedCount == requiredCount
                    : matched.Count > 0;
        return new CustomFormatDefinitionMatch(isMatch, matched, missed);
    }

    private static IReadOnlyList<CustomFormatDefinitionCondition> ParseConditions(string? rawConditions)
    {
        if (string.IsNullOrWhiteSpace(rawConditions))
        {
            return [];
        }

        var trimmed = rawConditions.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Array
                    && document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                {
                    return document.RootElement.EnumerateArray()
                        .Select(item => new CustomFormatDefinitionCondition(
                            "releaseTitle",
                            item.GetString() ?? string.Empty,
                            false,
                            true))
                        .ToArray();
                }

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var conditions = document.RootElement.EnumerateArray()
                    .Select(ParseJsonCondition)
                    .ToArray();
                return conditions.Length > 0 && conditions.All(condition => condition is not null)
                    ? conditions.Cast<CustomFormatDefinitionCondition>().ToArray()
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return trimmed
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseLegacyCondition)
            .ToArray();
    }

    private static CustomFormatDefinitionCondition? ParseJsonCondition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = ReadString(element, "type");
        var value = ReadString(element, "value");
        string? externalGroup = null;
        if (string.IsNullOrWhiteSpace(type))
        {
            var implementation = ReadString(element, "implementation");
            var external = MapExternalCondition(implementation, ReadExternalFieldValue(element));
            type = external?.Type;
            value = external?.Value;
            externalGroup = implementation;
        }

        if (string.IsNullOrWhiteSpace(type)
            || string.IsNullOrWhiteSpace(value)
            || !SupportedTypes.Contains(type))
        {
            return null;
        }

        return new CustomFormatDefinitionCondition(
            type,
            value,
            ReadBoolean(element, "negate") ?? false,
            ReadBoolean(element, "required") ?? true,
            externalGroup);
    }

    private static (string Type, string Value)? MapExternalCondition(
        string? implementation,
        string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        return implementation?.Trim().ToLowerInvariant() switch
        {
            "releasetitlespecification" => ("releaseTitle", value),
            "sourcespecification" when MapQualitySource(value) is { } source => ("source", source),
            "resolutionspecification" => ("resolution", value),
            "qualitymodifierspecification" when value == "5" => ("source", "remux"),
            "hdrspecification" => ("hdr", value),
            "codecspecification" => ("codec", value),
            "releasegroupspecification" => ("releaseGroup", value),
            _ => null
        };
    }

    private static string? MapQualitySource(string value)
        => value.ToLowerInvariant() switch
        {
            "1" => "cam",
            "2" => "telesync",
            "3" => "telecine",
            "4" => "workprint",
            "5" => "dvd",
            "6" => "hdtv",
            "7" => "web",
            "8" => "webrip",
            "9" => "bluray",
            "cam" or "telesync" or "telecine" or "workprint" or "dvd" or "hdtv"
                or "web" or "web-dl" or "webdl" or "webrip" or "bluray" or "blu-ray" => value,
            _ => null
        };

    private static string? ReadExternalFieldValue(JsonElement element)
    {
        if (TryGetProperty(element, "fields", out var fields))
        {
            if (fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
                {
                    if (string.Equals(ReadString(field, "name"), "value", StringComparison.OrdinalIgnoreCase)
                        && TryGetProperty(field, "value", out var fieldValue))
                    {
                        return ReadScalar(fieldValue);
                    }
                }

                var first = fields.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
                if (first.ValueKind == JsonValueKind.Object && TryGetProperty(first, "value", out var firstValue))
                {
                    return ReadScalar(firstValue);
                }
            }
            else if (fields.ValueKind == JsonValueKind.Object
                     && TryGetProperty(fields, "value", out var fieldValue))
            {
                return ReadScalar(fieldValue);
            }
        }

        var fieldsJson = ReadString(element, "fieldsJson");
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(fieldsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && TryGetProperty(document.RootElement, "value", out var value)
                ? ReadScalar(value)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? ReadScalar(value) : null;

    private static bool? ReadBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadScalar(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static CustomFormatDefinitionCondition ParseLegacyCondition(string rawCondition)
    {
        var trimmed = rawCondition.Trim();
        if (trimmed.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            return new CustomFormatDefinitionCondition(
                "releaseTitle",
                trimmed["regex:".Length..].Trim(),
                false,
                true);
        }

        var colonIndex = trimmed.IndexOf(':');
        return colonIndex > 0 && colonIndex < trimmed.Length - 1
            ? new CustomFormatDefinitionCondition(
                trimmed[..colonIndex].Trim(),
                trimmed[(colonIndex + 1)..].Trim(),
                false,
                true)
            : new CustomFormatDefinitionCondition("releaseTitle", trimmed, false, true);
    }

    private static bool EvaluateSingleCondition(
        string releaseName,
        string lower,
        string? inferredGroup,
        CustomFormatDefinitionCondition condition)
    {
        var value = condition.Value?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        return condition.Type?.Trim().ToLowerInvariant() switch
        {
            "releasetitle" or "regex" => MatchReleaseTitle(releaseName, lower, value),
            "source" => MatchSource(lower, value),
            "resolution" => MatchResolution(lower, value),
            "hdr" => MatchHdr(lower, value),
            "codec" => MatchCodec(lower, value),
            "releasegroup" => MatchReleaseGroup(inferredGroup, value),
            "language" => lower.Contains(value.ToLowerInvariant(), StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool MatchReleaseTitle(string releaseName, string lower, string pattern)
    {
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            pattern = pattern["regex:".Length..].Trim();
        }

        if (pattern.AsSpan().ContainsAny(['*', '+', '?', '(', '|', '[', '\\', '^', '$']))
        {
            try
            {
                return Regex.IsMatch(
                    releaseName,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                // An imported invalid regex remains usable as a literal rule.
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return lower.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static bool MatchSource(string lower, string value)
        => value.ToLowerInvariant() switch
        {
            "bluray" => lower.Contains("bluray", StringComparison.Ordinal) || lower.Contains("blu-ray", StringComparison.Ordinal),
            "remux" => lower.Contains("remux", StringComparison.Ordinal),
            "web" => lower.Contains("web-dl", StringComparison.Ordinal)
                     || lower.Contains("webdl", StringComparison.Ordinal)
                     || (lower.Contains("web", StringComparison.Ordinal) && !lower.Contains("webrip", StringComparison.Ordinal)),
            "webrip" => lower.Contains("webrip", StringComparison.Ordinal),
            "hdtv" => lower.Contains("hdtv", StringComparison.Ordinal),
            "dvd" => lower.Contains("dvdrip", StringComparison.Ordinal) || lower.Contains("dvd", StringComparison.Ordinal),
            var other => lower.Contains(other, StringComparison.Ordinal)
        };

    private static bool MatchResolution(string lower, string value)
        => value.ToLowerInvariant() switch
        {
            "2160p" => lower.Contains("2160", StringComparison.Ordinal) || lower.Contains("4k", StringComparison.Ordinal),
            "1080p" => lower.Contains("1080", StringComparison.Ordinal),
            "720p" => lower.Contains("720", StringComparison.Ordinal),
            "480p" => lower.Contains("480", StringComparison.Ordinal),
            var other => lower.Contains(other, StringComparison.Ordinal)
        };

    private static bool MatchHdr(string lower, string value)
        => value.ToLowerInvariant() switch
        {
            "dv" or "dolbyvision" => lower.Contains(".dv.", StringComparison.Ordinal)
                                        || lower.Contains("dolby.vision", StringComparison.Ordinal)
                                        || lower.Contains("dv.", StringComparison.Ordinal)
                                        || lower.Contains(".dovi", StringComparison.Ordinal),
            "hdr10plus" or "hdr10+" => lower.Contains("hdr10+", StringComparison.Ordinal) || lower.Contains("hdr10plus", StringComparison.Ordinal),
            "hdr10" or "hdr" => lower.Contains("hdr10", StringComparison.Ordinal) || lower.Contains(".hdr.", StringComparison.Ordinal),
            "hlg" => lower.Contains("hlg", StringComparison.Ordinal),
            var other => lower.Contains(other, StringComparison.Ordinal)
        };

    private static bool MatchCodec(string lower, string value)
        => value.ToLowerInvariant() switch
        {
            "x265" or "hevc" => lower.Contains("x265", StringComparison.Ordinal) || lower.Contains("h265", StringComparison.Ordinal) || lower.Contains("hevc", StringComparison.Ordinal),
            "x264" or "avc" => lower.Contains("x264", StringComparison.Ordinal) || lower.Contains("h264", StringComparison.Ordinal) || lower.Contains("avc", StringComparison.Ordinal),
            "av1" => lower.Contains("av1", StringComparison.Ordinal),
            "xvid" or "divx" => lower.Contains("xvid", StringComparison.Ordinal) || lower.Contains("divx", StringComparison.Ordinal),
            var other => lower.Contains(other, StringComparison.Ordinal)
        };

    private static bool MatchReleaseGroup(string? inferredGroup, string value)
        => !string.IsNullOrWhiteSpace(inferredGroup)
           && MatchReleaseTitle(inferredGroup, inferredGroup.ToLowerInvariant(), value);

    private static string? InferReleaseGroup(string releaseName)
    {
        var match = ReleaseGroupRegex().Match(releaseName);
        return match.Success ? match.Groups["group"].Value : null;
    }

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,20})$")]
    private static partial Regex ReleaseGroupRegex();
}

public sealed record CustomFormatDefinitionMatch(
    bool IsMatch,
    IReadOnlyList<string> MatchedConditions,
    IReadOnlyList<string> MissedConditions);

public sealed record CustomFormatDefinitionCondition(
    string Type,
    string Value,
    bool Negate = false,
    bool Required = true,
    string? ExternalGroup = null);
