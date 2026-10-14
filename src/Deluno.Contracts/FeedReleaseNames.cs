using System.Text;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// What a release name in an indexer's feed says it is: a film's title and
/// year, or a show and an episode (#587).
/// </summary>
/// <param name="Title">The title part, as written, separators and all.</param>
public sealed record ParsedFeedRelease(string Title, int? Year, int? Season, int? Episode)
{
    public bool IsEpisode => Season is not null && Episode is not null;
}

/// <summary>
/// Reading a scene release name well enough to know which title it is for.
///
/// <para>Deliberately small. RSS sync only has to recognise the title; the
/// search it then runs reads every release properly, with the profile, the
/// blocklist and every other rule, so a loose match costs one search and a
/// missed one costs only the wait for the title's turn in the cycle.</para>
/// </summary>
public static partial class FeedReleaseNames
{
    public static ParsedFeedRelease? Parse(string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return null;
        }

        var name = releaseName.Trim();
        foreach (var marker in ReleaseNameReader.ReadEpisodes(name))
        {
            // The title is what stands before the marker, which must follow a separator.
            if (marker.Index > 0 && name[marker.Index - 1] is ' ' or '.' or '_' or '-' or '	'
                && name[..marker.Index].Trim(' ', '.', '_', '-', '	') is { Length: > 0 })
            {
                return new ParsedFeedRelease(name[..marker.Index].TrimEnd(' ', '.', '_', '-', '	'), null, marker.Season, marker.FirstEpisode);
            }
        }

        // The last year in the name, so "2001.A.Space.Odyssey.1968" is 1968.
        var film = FilmPattern().Match(name);
        return film.Success
            ? new ParsedFeedRelease(
                film.Groups["title"].Value,
                int.Parse(film.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture),
                null,
                null)
            : null;
    }

    /// <summary>
    /// A title reduced to what two spellings of it share: lower case, "&amp;"
    /// as "and", apostrophes dropped ("Wouldn't" and "Wouldnt" agree), every
    /// other separator a single space.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = title.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\'' or '’')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A show's title with a trailing year taken off, for names such as
    /// "Doctor.Who.2005.S01E01" where the year tells same-named shows apart.
    /// </summary>
    public static string WithoutTrailingYear(string normalizedTitle)
        => TrailingYearPattern().Replace(normalizedTitle, string.Empty).Trim();

    [GeneratedRegex(@"^(?<title>.+)[\s._\-(\[]+(?<year>" + ReleaseNameReader.YearDigits + @")(?:[\s._\-)\]]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FilmPattern();

    [GeneratedRegex(@"\s" + ReleaseNameReader.YearDigits + "$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();
}
