using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// The versioned release-preference API uses stable camel-case strings for
/// enum values. This is deliberately separate from the host's default JSON
/// settings so adding a typed contract cannot change unrelated endpoints.
/// </summary>
public static class ReleasePreferenceJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record ReleasePreferencePlanCompilation(
    string RegistryVersion,
    string ProfileId,
    string ProfileName,
    ReleasePreferencePlan Plan,
    string PlanHash,
    IReadOnlyList<LegacyPreferenceRuleTranslation> AdvancedRules,
    IReadOnlyList<string> Warnings,
    bool RequiresReview,
    DateTimeOffset StoredUtc);
