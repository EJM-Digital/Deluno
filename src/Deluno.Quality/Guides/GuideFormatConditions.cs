using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Quality.Guides;

/// <summary>
/// The conditions a rule made from a guide format carries, decided here and
/// nowhere else. The guide picker, presets, profile starters, the setup guide
/// and the compiled guide plans each used to build them their own way - one
/// turned every TRaSH pattern into a required title condition - so a rule's
/// meaning depended on which screen made it.
/// </summary>
public static class GuideFormatConditions
{
    /// <summary>
    /// A genuine TRaSH format is defined by its upstream specifications, not by
    /// patterns Deluno wrote. A Deluno-owned extra has only its patterns.
    /// </summary>
    public static bool IsGenuine(GuideCustomFormat format)
        => format.SourceMatcherClauses is { Count: > 0 } && (format.Patterns?.Count ?? 0) == 0;

    /// <summary>
    /// For a genuine format: TRaSH's specifications exactly, in the shape
    /// Radarr and Sonarr export them, so they are evaluated with TRaSH's own
    /// grouping (every required clause, and one of each kind). For an extra:
    /// its patterns, any one of which is enough.
    /// </summary>
    public static string For(GuideCustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return IsGenuine(format)
            ? FromSourceClauses(format.SourceMatcherClauses!)
            : JsonSerializer.Serialize(format.Patterns ?? []);
    }

    public static string FromSourceClauses(IReadOnlyList<GuideSourceMatcherClause> clauses)
        => new JsonArray(clauses
            .Select(clause => (JsonNode)new JsonObject
            {
                ["name"] = clause.Name,
                ["implementation"] = clause.Implementation,
                ["negate"] = clause.Negate,
                ["required"] = clause.Required,
                ["fields"] = JsonNode.Parse(clause.FieldsJson)
            })
            .ToArray()).ToJsonString();

    /// <summary>
    /// Whether a rule for <paramref name="mediaType"/> can be made from this
    /// format. TRaSH publishes a movies copy and a TV copy of most formats,
    /// under different ids; a Deluno extra with no media types serves both.
    /// </summary>
    public static bool AppliesTo(GuideCustomFormat format, string? mediaType)
    {
        if (format.MediaTypes is not { Count: > 0 } mediaTypes)
        {
            return true;
        }

        var normalized = MediaPolicyCatalog.NormalizeMediaType(mediaType);
        return mediaTypes.Any(item => string.Equals(
            MediaPolicyCatalog.NormalizeMediaType(item),
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when stored conditions are Radarr/Sonarr specifications.</summary>
    public static bool IsSpecificationShaped(string? conditions)
    {
        if (string.IsNullOrWhiteSpace(conditions) || !conditions.TrimStart().StartsWith('['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(conditions);
            return document.RootElement.ValueKind == JsonValueKind.Array
                   && document.RootElement.GetArrayLength() > 0
                   && document.RootElement.EnumerateArray().All(item =>
                       item.ValueKind == JsonValueKind.Object
                       && item.EnumerateObject().Any(property =>
                           string.Equals(property.Name, "implementation", StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
