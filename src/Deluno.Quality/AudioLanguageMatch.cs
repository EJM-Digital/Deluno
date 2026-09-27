namespace Deluno.Quality;

/// <summary>
/// Matches a probed audio stream's own language tag (ffprobe's metadata,
/// usually a two- or three-letter ISO 639 code such as "eng" or "en") against
/// one of <see cref="ProfileLanguagePreference"/>'s codes.
///
/// <para>Bridges two code spaces Deluno already speaks: the release-name
/// detection traits, in the short codes <see cref="ProfileLanguageCodes"/>
/// uses ("en", "ja", ...), and whatever a media file's own container actually
/// tags each track with. Used at Checkpoint A (#917, "missing the audio
/// language you want") and Checkpoint B (#919, "lost every track in a wanted
/// language") so a probed file and a release name never disagree about what a
/// language code means.</para>
///
/// <para>Only the concrete languages have a real-world tag to match against.
/// "original", "multi" and "dubbed" describe how a release was assembled, not
/// a language a track is tagged with, so they never match here — there is
/// nothing in a probed stream's own metadata that could answer that
/// question.</para>
/// </summary>
public static class AudioLanguageMatch
{
    private static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["en", "eng"],
        ["ja"] = ["ja", "jpn", "jp"],
        ["de"] = ["de", "deu", "ger"],
        ["fr"] = ["fr", "fra", "fre"],
        ["es"] = ["es", "spa"]
    };

    /// <summary>Whether any of a file's own audio-track language tags satisfies the wanted code.</summary>
    public static bool AnyMatches(IEnumerable<string?> streamLanguages, string wantedCode)
    {
        if (!Aliases.TryGetValue(wantedCode.Trim().ToLowerInvariant(), out var codes))
        {
            return false;
        }

        return streamLanguages.Any(language =>
            !string.IsNullOrWhiteSpace(language) && codes.Contains(language.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether this code names a real language a probed track could be tagged
    /// with, rather than "original", "multi" or "dubbed" -- release-assembly
    /// concepts a container's own language tag can never answer.
    /// </summary>
    public static bool Recognizes(string code) => Aliases.ContainsKey(code.Trim().ToLowerInvariant());
}
