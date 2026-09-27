using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Contracts;

/// <summary>
/// The three answers a person can give about hearing-impaired subtitles for one
/// language — Bazarr's per-language required/not-required/only, folded into the
/// words this rung actually asks: does the plain track win a tie, or the
/// hearing-impaired one, or is hearing-impaired refused outright.
/// </summary>
public static class SubtitleHearingImpairedPreferences
{
    /// <summary>Never take a hearing-impaired track for this language.</summary>
    public const string PlainOnly = "plain-only";

    /// <summary>
    /// Either is coverage. A plain track wins a tie. The default, and what
    /// Deluno has always done.
    /// </summary>
    public const string PlainPreferred = "plain-preferred";

    /// <summary>Either is coverage, but a hearing-impaired track wins a tie.</summary>
    public const string HearingImpairedPreferred = "hi-preferred";

    /// <summary>
    /// Anything unrecognised reads as <see cref="PlainPreferred"/> — the
    /// existing behaviour — for the same reason every other stored preference
    /// in this codebase refuses to guess at a stricter reading: a stray value
    /// silently narrowing what gets fetched would never say why.
    /// </summary>
    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            PlainOnly => PlainOnly,
            HearingImpairedPreferred => HearingImpairedPreferred,
            _ => PlainPreferred
        };
}

/// <summary>
/// What one wanted language asks for beyond its place in the list — whether a
/// hearing-impaired track is refused, preferred, or just coverage like any
/// other, and whether a forced track is worth fetching as an extra file
/// alongside it.
///
/// <para>One row per language, keyed by <see cref="Language"/>. A language with
/// no row here gets the defaults — <see cref="SubtitleHearingImpairedPreferences.PlainPreferred"/>
/// and no forced fetch — which is exactly what Deluno did before this existed,
/// so nobody who has never opened this setting sees anything change.</para>
/// </summary>
public sealed record SubtitleLanguageTypePreference(
    string Language,
    string HearingImpaired = SubtitleHearingImpairedPreferences.PlainPreferred,
    /// <summary>
    /// Also fetch a forced track for this language as an extra file alongside
    /// the full one. Forced subtitles are never counted as coverage — a file
    /// whose only English is forced still has none of the language you asked
    /// for — so this never changes what the bar under a poster counts.
    /// </summary>
    bool IncludeForced = false);

/// <summary>Canonical persistence for a library's per-language subtitle-type preferences.</summary>
public static class SubtitleLanguageTypePreferenceCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Normalizes every language code and hearing-impaired value, drops a row
    /// for a language nobody could name, and keeps the last row for a language
    /// listed twice — the same "one code wins" rule <see cref="SubtitleLanguages"/>
    /// applies everywhere else a list of them is read.
    /// </summary>
    public static IReadOnlyList<SubtitleLanguageTypePreference> Normalize(
        IReadOnlyList<SubtitleLanguageTypePreference>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return [];
        }

        var byLanguage = new Dictionary<string, SubtitleLanguageTypePreference>(StringComparer.Ordinal);
        foreach (var preference in preferences)
        {
            var language = SubtitleLanguages.Normalize(preference.Language);
            if (language is null || language == SubtitleLanguages.Unknown)
            {
                continue;
            }

            byLanguage[language] = preference with
            {
                Language = language,
                HearingImpaired = SubtitleHearingImpairedPreferences.Normalize(preference.HearingImpaired)
            };
        }

        return [.. byLanguage.Values.OrderBy(preference => preference.Language, StringComparer.Ordinal)];
    }

    public static string? Serialize(IReadOnlyList<SubtitleLanguageTypePreference>? preferences)
    {
        var normalized = Normalize(preferences);
        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static IReadOnlyList<SubtitleLanguageTypePreference> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<IReadOnlyList<SubtitleLanguageTypePreference>>(json, JsonOptions));
        }
        catch (JsonException)
        {
            // A preference list nobody can read is not a preference list.
            // Falling back to the defaults for every language is the same
            // failure mode every other codec in this file chooses.
            return [];
        }
    }

    /// <summary>
    /// The preference for one language, or the defaults when nobody set one.
    /// </summary>
    public static SubtitleLanguageTypePreference For(
        IReadOnlyList<SubtitleLanguageTypePreference> preferences, string language)
    {
        var normalized = SubtitleLanguages.Normalize(language) ?? language;
        foreach (var preference in preferences)
        {
            if (string.Equals(preference.Language, normalized, StringComparison.Ordinal))
            {
                return preference;
            }
        }

        return new SubtitleLanguageTypePreference(normalized);
    }
}
