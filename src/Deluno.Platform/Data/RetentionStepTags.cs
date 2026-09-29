using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Platform.Data;

/// <summary>
/// The tags a retention rule's steps name (#1066). A step is a
/// <c>field:operator:value</c> condition; only the library "tag" field names a
/// Deluno tag, and its value is one or more tags joined with <c>|</c>. Every other
/// condition (a server label, a rating, a date) is left exactly as written.
/// Platform cannot see the Retention project, so this reads the stored JSON
/// directly: a list of steps, each a condition or a block with its own steps.
/// </summary>
public static class RetentionStepTags
{
    private const string TagField = "tag";

    /// <summary>Every tag the steps name, once each, in the order first met.</summary>
    public static IReadOnlyList<string> Extract(string? stepsJson)
    {
        var tags = new List<string>();
        if (Parse(stepsJson) is { } root)
        {
            Walk(root, condition =>
            {
                if (TryTagValues(condition, out _, out var values))
                {
                    foreach (var value in values.Where(v => !tags.Contains(v, StringComparer.OrdinalIgnoreCase)))
                    {
                        tags.Add(value);
                    }
                }

                return null;
            });
        }

        return tags;
    }

    /// <summary>
    /// The steps with the tag renamed in every tag condition. Returns the original
    /// text untouched when nothing named the tag or the text is not readable.
    /// </summary>
    public static string Rename(string stepsJson, string previous, string next)
    {
        if (Parse(stepsJson) is not { } root)
        {
            return stepsJson;
        }

        var changed = false;
        Walk(root, condition =>
        {
            if (!TryTagValues(condition, out var prefix, out var values)
                || !values.Contains(previous, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var renamed = values
                .Select(value => string.Equals(value, previous, StringComparison.OrdinalIgnoreCase) ? next : value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            changed = true;
            return prefix + string.Join("|", renamed);
        });

        return changed ? root.ToJsonString() : stepsJson;
    }

    private static JsonArray? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Visits every step's condition; a non-null result replaces it.</summary>
    private static void Walk(JsonArray steps, Func<string, string?> visit)
    {
        foreach (var step in steps.OfType<JsonObject>())
        {
            foreach (var property in step.ToList())
            {
                if (string.Equals(property.Key, "condition", StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (visit(text) is { } replacement)
                    {
                        step[property.Key] = replacement;
                    }
                }
                else if (string.Equals(property.Key, "steps", StringComparison.OrdinalIgnoreCase) && property.Value is JsonArray inner)
                {
                    Walk(inner, visit);
                }
            }
        }
    }

    private static bool TryTagValues(string condition, out string prefix, out string[] values)
    {
        prefix = string.Empty;
        values = [];
        var parts = condition.Split(':', 3);
        if (parts.Length < 3 || !string.Equals(parts[0].Trim(), TagField, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        prefix = $"{parts[0]}:{parts[1]}:";
        values = parts[2].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }
}
