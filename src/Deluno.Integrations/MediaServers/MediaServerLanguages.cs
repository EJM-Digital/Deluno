namespace Deluno.Integrations.MediaServers;

/// <summary>
/// One spelling for a language, whatever a server or a person calls it: "eng",
/// "en" and "English" are all "English" (#1003). A file's audio tracks and a
/// rule's wanted language go through the same function, so they cannot disagree.
/// </summary>
public static class MediaServerLanguages
{
    private static readonly (string Name, string[] Codes)[] Known =
    [
        ("English", ["en", "eng"]),
        ("Japanese", ["ja", "jpn", "jp"]),
        ("German", ["de", "deu", "ger"]),
        ("French", ["fr", "fra", "fre"]),
        ("Spanish", ["es", "spa"]),
        ("Italian", ["it", "ita"]),
        ("Portuguese", ["pt", "por"]),
        ("Russian", ["ru", "rus"]),
        ("Chinese", ["zh", "zho", "chi", "cmn", "yue"]),
        ("Korean", ["ko", "kor"]),
        ("Dutch", ["nl", "nld", "dut"]),
        ("Swedish", ["sv", "swe"]),
        ("Norwegian", ["no", "nor", "nob", "nb"]),
        ("Danish", ["da", "dan"]),
        ("Finnish", ["fi", "fin"]),
        ("Polish", ["pl", "pol"]),
        ("Czech", ["cs", "ces", "cze"]),
        ("Hungarian", ["hu", "hun"]),
        ("Greek", ["el", "ell", "gre"]),
        ("Turkish", ["tr", "tur"]),
        ("Arabic", ["ar", "ara"]),
        ("Hebrew", ["he", "heb"]),
        ("Hindi", ["hi", "hin"]),
        ("Thai", ["th", "tha"]),
        ("Vietnamese", ["vi", "vie"]),
        ("Indonesian", ["id", "ind"]),
        ("Ukrainian", ["uk", "ukr"]),
        ("Romanian", ["ro", "ron", "rum"]),
        ("Bulgarian", ["bg", "bul"]),
        ("Croatian", ["hr", "hrv"]),
        ("Serbian", ["sr", "srp"]),
        ("Icelandic", ["is", "isl", "ice"]),
        ("Latin", ["la", "lat"]),
        ("Tagalog", ["tl", "tgl", "fil", "filipino"]),
        ("Persian", ["fa", "fas", "per"]),
        ("Catalan", ["ca", "cat"]),
        ("Malay", ["ms", "msa", "may"]),
        ("Tamil", ["ta", "tam"]),
        ("Telugu", ["te", "tel"]),
        ("Urdu", ["ur", "urd"]),
        ("Bengali", ["bn", "ben"]),
        ("Slovak", ["sk", "slk", "slo"]),
        ("Slovenian", ["sl", "slv"]),
        ("Lithuanian", ["lt", "lit"]),
        ("Latvian", ["lv", "lav"]),
        ("Estonian", ["et", "est"]),
        ("Afrikaans", ["af", "afr"]),
        ("Welsh", ["cy", "cym", "wel"]),
        ("Irish", ["ga", "gle"]),
        ("Basque", ["eu", "eus", "baq"]),
        ("Galician", ["gl", "glg"]),
        ("Macedonian", ["mk", "mkd", "mac"]),
        ("Albanian", ["sq", "sqi", "alb"]),
        ("Armenian", ["hy", "hye", "arm"]),
        ("Georgian", ["ka", "kat", "geo"]),
        ("Malayalam", ["ml", "mal"]),
        ("Kannada", ["kn", "kan"]),
        ("Marathi", ["mr", "mar"]),
        ("Punjabi", ["pa", "pan"]),
        ("Gujarati", ["gu", "guj"]),
        ("Nepali", ["ne", "nep"]),
        ("Sinhala", ["si", "sin"]),
        ("Swahili", ["sw", "swa"]),
        ("Kazakh", ["kk", "kaz"]),
        ("Mongolian", ["mn", "mon"]),
        ("Burmese", ["my", "mya", "bur"]),
        ("Khmer", ["km", "khm"]),
        ("Lao", ["lo", "lao"]),
        ("Bosnian", ["bs", "bos"]),
        ("Belarusian", ["be", "bel"]),
        ("Azerbaijani", ["az", "aze"]),
        ("Uzbek", ["uz", "uzb"])
    ];

    private static readonly Dictionary<string, string> ByCode = BuildLookup();

    private static Dictionary<string, string> BuildLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, codes) in Known)
        {
            lookup[name] = name;
            foreach (var code in codes)
            {
                lookup[code] = name;
            }
        }

        return lookup;
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "und", "undetermined", "unknown", "unk", "undefined", "mis", "uncoded", "zxx", "mul", "multiple", "na", "n/a", "not applicable", "none"
    };

    /// <summary>
    /// The language as one plain English name. Null when the tag is empty, says
    /// nothing (undetermined, unknown, no linguistic content, several languages,
    /// the reserved private-use codes qaa to qtz), or is a language this table
    /// does not know: a tag that cannot be named is never guessed at.
    /// </summary>
    public static string? Name(string? tag)
    {
        var text = tag?.Trim();
        if (string.IsNullOrEmpty(text) || Placeholders.Contains(text))
        {
            return null;
        }

        // "en-GB" and "pt_BR" are the language before the region.
        var head = text.Split('-', '_')[0];
        if (Placeholders.Contains(head) || (head.Length == 3 && head[0] is 'q' or 'Q' && head[1] is >= 'a' and <= 't' or >= 'A' and <= 'T'))
        {
            return null;
        }

        if (ByCode.TryGetValue(text, out var whole)) return whole;
        return ByCode.TryGetValue(head, out var name) ? name : null;
    }

    /// <summary>
    /// The languages of a file's audio tracks. Null unless every track has a
    /// language tag: half a list would let "has no French" be true of a file
    /// whose untagged track is French.
    /// </summary>
    public static IReadOnlyList<string>? OfAudioTracks(IReadOnlyList<string?> trackTags)
    {
        if (trackTags.Count == 0)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var tag in trackTags)
        {
            if (Name(tag) is not { } name)
            {
                return null;
            }

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
