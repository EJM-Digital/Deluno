using System.Text.Json;

namespace Deluno.Quality;

/// <summary>
/// "Preferred language: English (then Any)." A profile's own answer to which
/// language it searches and downloads toward, on top of any language custom
/// format the profile has separately selected (#769).
///
/// <para>Codes match the audio-language traits Deluno already recognises
/// (<c>en</c>, <c>original</c>, <c>multi</c>, <c>dubbed</c>, <c>ja</c>,
/// <c>de</c>, <c>fr</c>, <c>es</c>; see
/// <c>PreferenceTraitRegistry</c>), so a preference here and a custom format
/// condition never disagree about what a language code means.</para>
/// </summary>
public sealed record ProfileLanguagePreference(
    /// <summary>Best-first. Empty means the profile has no language opinion.</summary>
    IReadOnlyList<string>? PreferredLanguages = null,
    /// <summary>
    /// False (the default): prefer the listed languages, ranking a matching
    /// release above a non-matching one, but do not refuse a release for its
    /// language alone. True: refuse a release that does not match the most
    /// preferred language.
    /// </summary>
    bool RequireLanguage = false)
{
    public bool IsEmpty => (PreferredLanguages is null || PreferredLanguages.Count == 0) && !RequireLanguage;

    public ProfileLanguagePreference Normalize()
    {
        var normalized = (PreferredLanguages ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return this with
        {
            PreferredLanguages = normalized,
            // "Require" a language nobody named is not an answer anybody
            // meant to give; it reads as no opinion, same as an empty list.
            RequireLanguage = RequireLanguage && normalized.Length > 0
        };
    }
}

public static class ProfileLanguagePreferenceCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProfileLanguagePreference? preference)
    {
        var normalized = preference?.Normalize();
        return normalized is null || normalized.IsEmpty
            ? string.Empty
            : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static ProfileLanguagePreference? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileLanguagePreference>(json, JsonOptions)?.Normalize();
        }
        catch (JsonException)
        {
            // A preference nobody can read is no preference, not a rule that
            // silently refuses every release once RequireLanguage was true.
            return null;
        }
    }
}

/// <summary>
/// Plain labels for the language codes <see cref="ProfileLanguagePreference"/>
/// uses. Detection of a release's actual language lives in
/// <c>ReleasePreferenceFactFactory</c> and the ranking or hard-gate effect of
/// a preference lives in <c>ReleasePreferencePlanFactory</c>, both against the
/// same <c>audio.language.*</c> traits this looks up - so a language is never
/// named or matched two different ways.
/// </summary>
public static class ProfileLanguageCodes
{
    public static string Label(string code)
        => Deluno.Quality.ReleasePreferences.PreferenceTraitRegistry.Current.TryResolve($"audio.language.{code}", out var trait)
            ? trait.DisplayName
            : code;
}
