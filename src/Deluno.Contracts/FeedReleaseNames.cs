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
        var episode = EpisodePattern().Match(name);
        if (episode.Success)
        {
            return new ParsedFeedRelease(
                episode.Groups["title"].Value,
                null,
                int.Parse(episode.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(episode.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture));
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

    [GeneratedRegex(@"^(?<title>.+?)[\s._-]+S(?<season>\d{1,2})[\s._-]?E(?<episode>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"^(?<title>.+)[\s._\-(\[]+(?<year>(?:19|20)\d{2})(?:[\s._\-)\]]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FilmPattern();

    [GeneratedRegex(@"\s(?:19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();
}
