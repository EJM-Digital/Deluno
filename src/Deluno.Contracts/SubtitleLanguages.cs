namespace Deluno.Contracts;

/// <summary>
/// One vocabulary for a subtitle language, because there are three of them in
/// the wild and Deluno has to hold all three at once.
///
/// A library's wanted list is stored as ISO 639-1 (<c>en</c>). ffprobe reports
/// the stream tag as ISO 639-2 (<c>eng</c>, and for a dozen languages two
/// different 639-2 codes for the same language — <c>fre</c> and <c>fra</c>).
/// A subtitle file sitting beside a video is named by whoever made it, which in
/// practice means any of those plus the English name of the language
/// (<c>.english.srt</c>) and an occasional locale (<c>pt-BR</c>).
///
/// If those stayed three vocabularies, a movie with <c>eng</c> embedded and
/// <c>en</c> wanted would read as missing, and Subber would fetch a subtitle
/// the file already contains. So every code entering Deluno passes through
/// <see cref="Normalize"/> and comes out as one code.
/// </summary>
public static class SubtitleLanguages
{
    /// <summary>
    /// A subtitle whose language nobody recorded — most often a bare
    /// <c>Movie.srt</c> beside the video.
    ///
    /// Deliberately not guessed at. Reading it as the library's first wanted
    /// language would be right most of the time and, when it was wrong, would
    /// stop Deluno fetching a language you had asked for and never say why.
    /// It is stored as a fact, and it does not count towards anything.
    /// </summary>
    public const string Unknown = "und";

    /// <summary>
    /// The languages Deluno can name, in the order a picker should offer them:
    /// the ones subtitle providers actually carry, most-used first, then the
    /// rest alphabetically.
    /// </summary>
    public static readonly IReadOnlyList<SubtitleLanguage> All =
    [
        new("en", "English", ["eng"]),
        new("es", "Spanish", ["spa"]),
        new("fr", "French", ["fre", "fra"]),
        new("de", "German", ["ger", "deu"]),
        new("it", "Italian", ["ita"]),
        new("pt", "Portuguese", ["por"]),
        new("nl", "Dutch", ["dut", "nld"]),
        new("ja", "Japanese", ["jpn"]),
        new("ko", "Korean", ["kor"]),
        new("zh", "Chinese", ["chi", "zho"]),
        new("ru", "Russian", ["rus"]),
        new("ar", "Arabic", ["ara"]),
        new("hi", "Hindi", ["hin"]),
        new("pl", "Polish", ["pol"]),
        new("tr", "Turkish", ["tur"]),
        new("sv", "Swedish", ["swe"]),
        new("da", "Danish", ["dan"]),
        new("no", "Norwegian", ["nor"]),
        new("fi", "Finnish", ["fin"]),
        new("is", "Icelandic", ["ice", "isl"]),
        new("cs", "Czech", ["cze", "ces"]),
        new("sk", "Slovak", ["slo", "slk"]),
        new("hu", "Hungarian", ["hun"]),
        new("ro", "Romanian", ["rum", "ron"]),
        new("bg", "Bulgarian", ["bul"]),
        new("el", "Greek", ["gre", "ell"]),
        new("he", "Hebrew", ["heb"]),
        new("uk", "Ukrainian", ["ukr"]),
        new("sr", "Serbian", ["srp"]),
        new("hr", "Croatian", ["hrv"]),
        new("sl", "Slovenian", ["slv"]),
        new("et", "Estonian", ["est"]),
        new("lv", "Latvian", ["lav"]),
        new("lt", "Lithuanian", ["lit"]),
        new("th", "Thai", ["tha"]),
        new("vi", "Vietnamese", ["vie"]),
        new("id", "Indonesian", ["ind"]),
        new("ms", "Malay", ["may", "msa"]),
        new("fa", "Persian", ["per", "fas"]),
        new("ca", "Catalan", ["cat"]),
        new("gl", "Galician", ["glg"]),
        new("eu", "Basque", ["baq", "eus"])
    ];

    private static readonly Dictionary<string, string> ByAnyName = BuildIndex();

    /// <summary>
    /// Reads any of the three vocabularies and returns the one code, or
    /// <c>null</c> when the value is not a language at all.
    ///
    /// <c>null</c> rather than <see cref="Unknown"/> is what lets a sidecar's
    /// filename be parsed: <c>Movie.en.forced.srt</c> has two tags between the
    /// title and the extension, and only one of them is a language. A caller
    /// that has a language-shaped hole to fill uses <see cref="Unknown"/> for
    /// itself.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (ByAnyName.TryGetValue(trimmed, out var direct))
        {
            return direct;
        }

        // A locale, as ffprobe and Plex-era filenames both emit it: pt-BR,
        // zh_Hans, en-US. The region says which flavour, not which language,
        // and Deluno asks for languages.
        var separator = trimmed.IndexOfAny(['-', '_']);
        if (separator > 0 && ByAnyName.TryGetValue(trimmed[..separator], out var localised))
        {
            return localised;
        }

        return trimmed is "und" or "unknown" or "unk" or "mis" or "zxx" ? Unknown : null;
    }

    /// <summary>
    /// Reads a stored comma-separated list into ordered, de-duplicated codes.
    ///
    /// Order is the preference and is preserved. A duplicate is dropped because
    /// it would inflate what the bar under a poster says was asked for, and no
    /// title is ever twice subtitled in one language.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var languages = new List<string>();
        foreach (var part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = Normalize(part);
            if (code is null || code == Unknown)
            {
                continue;
            }

            if (seen.Add(code))
            {
                languages.Add(code);
            }
        }

        return languages;
    }

    public static string DisplayName(string? code)
    {
        var normalized = Normalize(code);
        if (normalized is null or Unknown)
        {
            return "Unknown";
        }

        foreach (var language in All)
        {
            if (language.Code == normalized)
            {
                return language.Name;
            }
        }

        return normalized.ToUpperInvariant();
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in All)
        {
            index[language.Code] = language.Code;
            index[language.Name.ToLowerInvariant()] = language.Code;
            foreach (var alias in language.Aliases)
            {
                index[alias] = language.Code;
            }
        }

        return index;
    }
}

public sealed record SubtitleLanguage(string Code, string Name, IReadOnlyList<string> Aliases);
