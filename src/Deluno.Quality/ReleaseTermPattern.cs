using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Deluno.Quality;

/// <summary>
/// One "must contain" / "must not contain" term of a release rule: plain words,
/// or a pattern written between slashes, <c>/pattern/i</c>.
///
/// <para>Same rule Sonarr's <c>PerlRegexFactory</c> and <c>TermMatcherService</c>
/// use (read in their source, not the wiki): a term of the form
/// <c>/pattern/modifiers</c> is a regular expression, anything else is a plain
/// case-insensitive "contains". The modifiers are <c>i</c> (ignore case), <c>m</c>,
/// <c>s</c>, <c>x</c> and <c>n</c>; no <c>i</c> means the pattern is case-sensitive,
/// as it is there. A term that does not start with a slash behaves exactly as it
/// always has, so every rule written before patterns existed still means the same.</para>
///
/// <para>Terms are separated by new lines or commas. A comma inside a slash pattern
/// (<c>/x{2,3}/</c>) does not split it.</para>
/// </summary>
public static class ReleaseTermPattern
{
    private static readonly ConcurrentDictionary<string, Regex?> Compiled = new(StringComparer.Ordinal);

    /// <summary>Splits rule text into its terms, keeping slash patterns whole.</summary>
    public static IReadOnlyList<string> Split(string? value, int maxLength = 300)
    {
        var terms = new List<string>();
        var text = value ?? string.Empty;
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var end = -1;
            if (text[index] == '/')
            {
                end = FindPatternEnd(text, index);
            }

            if (end < 0)
            {
                end = index;
                while (end < text.Length && text[end] != ',' && text[end] != '\n' && text[end] != '\r')
                {
                    end++;
                }
            }

            var term = text[index..end].Trim();
            if (term.Length > 0 && term.Length <= maxLength
                && !terms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term);
            }

            index = end;
        }

        return terms;
    }

    /// <summary>
    /// The end (exclusive) of a slash pattern starting at <paramref name="start"/>:
    /// the first closing slash followed only by modifier letters and then a
    /// separator or the end of the text. -1 when the text is not such a pattern.
    /// </summary>
    private static int FindPatternEnd(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r')
            {
                return -1;
            }

            if (text[i] != '/')
            {
                continue;
            }

            var j = i + 1;
            while (j < text.Length && text[j] is >= 'a' and <= 'z')
            {
                j++;
            }

            var k = j;
            while (k < text.Length && text[k] is ' ' or '\t')
            {
                k++;
            }

            if (k >= text.Length || text[k] is ',' or '\n' or '\r')
            {
                return j;
            }
        }

        return -1;
    }

    public static bool IsPattern(string term)
        => TrySplitPattern(term.Trim(), out _, out _);

    private static bool TrySplitPattern(string term, out string pattern, out string modifiers)
    {
        pattern = string.Empty;
        modifiers = string.Empty;
        if (term.Length < 3 || term[0] != '/')
        {
            return false;
        }

        var last = term.LastIndexOf('/');
        if (last < 2)
        {
            return false;
        }

        var tail = term[(last + 1)..];
        if (!tail.All(c => c is >= 'a' and <= 'z'))
        {
            return false;
        }

        pattern = term[1..last];
        modifiers = tail;
        return true;
    }

    /// <summary>
    /// Null when the term is fine (plain words, or a pattern that compiles);
    /// otherwise a plain sentence saying what is wrong with it.
    /// </summary>
    public static string? Validate(string term)
    {
        var trimmed = term.Trim();
        if (!TrySplitPattern(trimmed, out var pattern, out var modifiers))
        {
            return null;
        }

        foreach (var modifier in modifiers)
        {
            if (modifier is not ('i' or 'm' or 's' or 'x' or 'n'))
            {
                return $"'{trimmed}' ends with the letter '{modifier}', which a pattern cannot use. Patterns can end in i, m, s, x or n.";
            }
        }

        try
        {
            _ = new Regex(pattern, ToOptions(modifiers), TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException)
        {
            return $"'{trimmed}' is not a valid pattern. Check the brackets and slashes, or write it as plain words.";
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> matches <paramref name="term"/>. A
    /// pattern that cannot be read falls back to matching its text literally, so a
    /// bad pattern never silently lets everything through or blocks everything.
    /// </summary>
    public static bool IsMatch(string value, string term)
    {
        var trimmed = term.Trim();
        if (TrySplitPattern(trimmed, out _, out _)
            && Compiled.GetOrAdd(trimmed, Build) is { } regex)
        {
            try
            {
                return regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return value.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex? Build(string term)
    {
        if (!TrySplitPattern(term, out var pattern, out var modifiers)
            || modifiers.Any(m => m is not ('i' or 'm' or 's' or 'x' or 'n')))
        {
            return null;
        }

        var options = ToOptions(modifiers) | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            return new Regex(pattern, options, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static RegexOptions ToOptions(string modifiers)
    {
        var options = RegexOptions.None;
        foreach (var modifier in modifiers)
        {
            options |= modifier switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                'n' => RegexOptions.ExplicitCapture,
                _ => RegexOptions.None
            };
        }

        return options;
    }
}
