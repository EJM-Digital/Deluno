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
        ("Latin", ["la", "lat"])
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

    /// <summary>The language as one plain English name; an unfamiliar tag is kept as written. Null for nothing, or "undetermined".</summary>
    public static string? Name(string? tag)
    {
        var text = tag?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("und", StringComparison.OrdinalIgnoreCase) || text.Equals("undetermined", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("zxx", StringComparison.OrdinalIgnoreCase) || text.Equals("mul", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "en-GB" and "pt_BR" are the language before the region.
        var head = text.Split('-', '_')[0];
        if (ByCode.TryGetValue(text, out var whole)) return whole;
        return ByCode.TryGetValue(head, out var name) ? name : text;
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
