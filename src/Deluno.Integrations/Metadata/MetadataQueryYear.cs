using System.Globalization;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Metadata;

/// <summary>
/// Reads a trailing release year out of a typed title search.
///
/// <para>People say which film they mean the way a filename does:
/// <c>Night of the Living Dead 1968</c> or <c>Nosferatu (1922)</c>. Sent to
/// TMDb as one string, the year becomes part of the title and nothing
/// matches (#509). Split off and sent as the year filter, it picks the right
/// film out of its remakes.</para>
///
/// <para>A number that is part of the title has to survive: <c>1917</c> is
/// only a number, <c>Blade Runner 2049</c> is a year nobody has released a
/// film in yet, and <c>Wonder Woman 1984</c> is a real year that is also the
/// title. The first two are never split; the last is handled by the caller
/// searching the whole query again when the split finds nothing.</para>
/// </summary>
public static partial class MetadataQueryYear
{
    private const int EarliestFilmYear = 1870;

    /// <summary>
    /// How far ahead a year can be and still be a release year: announced
    /// titles are listed a few years out.
    /// </summary>
    private const int YearsAhead = 3;

    public static bool TrySplit(string? query, int currentYear, out string title, out int year)
    {
        title = string.Empty;
        year = 0;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var match = TrailingYear().Match(query.Trim());
        if (!match.Success)
        {
            return false;
        }

        var candidateTitle = match.Groups["title"].Value.Trim().TrimEnd('-', ':', ',').Trim();
        var candidateYear = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (candidateTitle.Length == 0 ||
            candidateYear < EarliestFilmYear ||
            candidateYear > currentYear + YearsAhead)
        {
            return false;
        }

        title = candidateTitle;
        year = candidateYear;
        return true;
    }

    [GeneratedRegex(@"^(?:(?<title>.*\S)\s*[\(\[](?<year>\d{4})[\)\]]|(?<title>.*\S)\s+(?<year>\d{4}))$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();
}
