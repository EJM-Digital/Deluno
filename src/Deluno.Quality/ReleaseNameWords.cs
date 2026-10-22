using System.Text.RegularExpressions;

namespace Deluno.Quality;

/// <summary>
/// Reads a release name as whole words. A word like "web", "dvd", "regional", "sample" or "english" only counts when it
/// stands alone between separators (. _ - space, brackets, slashes), never inside a longer word ("Webster", "Englishman"),
/// and a word that sits in the title (before the year or the season/episode marker) is the title's, not a tag
/// ("The.Sample.2020", "Regional.Manager.S01E01").
/// </summary>
public static class ReleaseNameWords
{
    private static readonly char[] Separators = [' ', '.', '-', '_', '(', ')', '[', ']', '{', '}', '/', '\\', '+', ','];
    private static readonly Regex Year = new(@"^(19|20)\d{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Episode = new(@"^(s\d{1,2}(e\d{1,4})*|s\d{1,2}|\d{1,2}x\d{2,3}|season|seasons)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Resolution = new(@"^(\d{3,4}[pi]|4k|uhd)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The words of the name, lower case, in order.</summary>
    public static IReadOnlyList<string> Split(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? []
            : name.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// How many leading words are the title: those before the first season/episode marker, else before the last year
    /// that comes before the resolution. Zero when the name has neither.
    /// </summary>
    public static int TitleWordCount(IReadOnlyList<string> words)
    {
        for (var i = 1; i < words.Count; i++)
        {
            if (Episode.IsMatch(words[i]))
            {
                return i;
            }
        }

        var firstResolution = -1;
        for (var i = 1; i < words.Count; i++)
        {
            if (Resolution.IsMatch(words[i]))
            {
                firstResolution = i;
                break;
            }
        }

        var searchEnd = firstResolution < 0 ? words.Count : firstResolution;
        for (var i = searchEnd - 1; i >= 1; i--)
        {
            if (Year.IsMatch(words[i]))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Words of the name that are not part of the title.</summary>
    public static IReadOnlyList<string> TagWords(string? name)
    {
        var words = Split(name);
        var title = TitleWordCount(words);
        return title == 0 ? words : words.Skip(title).ToList();
    }

    /// <summary>Whether any tag word (outside the title) equals one of the given words.</summary>
    public static bool HasTag(string? name, params string[] wanted)
        => HasTag(TagWords(name), wanted);

    public static bool HasTag(IReadOnlyList<string> tagWords, params string[] wanted)
        => tagWords.Any(word => wanted.Contains(word, StringComparer.Ordinal));

    /// <summary>Whether any tag word starts with one of the given prefixes (dvd matches dvdrip, dvd9).</summary>
    public static bool HasTagStartingWith(IReadOnlyList<string> tagWords, params string[] prefixes)
        => tagWords.Any(word => prefixes.Any(prefix => word.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>Whether two words in a row, anywhere among the tag words, are the given pair (blu + ray).</summary>
    public static bool HasPair(IReadOnlyList<string> tagWords, string first, string second)
    {
        for (var i = 0; i + 1 < tagWords.Count; i++)
        {
            if (tagWords[i] == first && tagWords[i + 1] == second)
            {
                return true;
            }
        }

        return false;
    }
}
