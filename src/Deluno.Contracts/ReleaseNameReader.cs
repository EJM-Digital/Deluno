using System.Globalization;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>An episode marker read out of a release name: S01E01, S01E01E02, S01E01-E03, S01E01-03 or 1x01 (#1307).</summary>
public sealed record EpisodeMarker(int Season, int FirstEpisode, int LastEpisode, int Index, int Length);

/// <summary>
/// The one reader of years, season and episode markers and daily dates in a release name, shared by
/// <see cref="ReleaseTitleMatch"/>, <see cref="FeedReleaseNames"/>, the worker's release-name parser and the library
/// import, so every one of them reads the same name the same way (#1307).
///
/// <para>A marker is bounded by anything that is not a letter or digit, so an underscore (a word character to
/// <c>\b</c>) separates exactly as a dot, a space or a dash does.</para>
/// </summary>
public static partial class ReleaseNameReader
{
    /// <summary>Not preceded by a letter or digit; unlike <c>\b</c> an underscore counts as a separator.</summary>
    public const string StartsToken = @"(?<![\p{L}\p{N}])";

    /// <summary>Not followed by a letter or digit.</summary>
    public const string EndsToken = @"(?![\p{L}\p{N}])";

    /// <summary>The digits of a year: 1900 to 2199.</summary>
    public const string YearDigits = @"(?:19|20|21)\d{2}";

    /// <summary>A year standing alone between non-digits. "2160p" is a resolution, not the year 2160.</summary>
    public const string YearToken = @"(?<![0-9])(?!2160[pi]" + EndsToken + ")" + YearDigits + @"(?![0-9])";

    /// <summary>The season and episode part of an episode marker: S01E01, S01E01E02, S01E01-E03, S01E01-03.</summary>
    public const string SeasonEpisodePattern =
        @"S(?<season>\d{1,2})[ ._-]?E(?<episode>\d{1,3})(?:(?:-?E(?<last>\d{1,3}))+|-(?<last>\d{1,3}))?";

    public const string NxNNPattern = @"(?<season>\d{1,2})x(?<episode>\d{2,3})";

    public const string SeasonPackPattern = @"S(?<season>\d{1,2})|(?:season|series|saison|temporada)[ ._-]?(?<season>\d{1,2})";

    /// <summary>The first episode marker in the text, or null.</summary>
    public static EpisodeMarker? ReadEpisode(string? text)
        => ReadEpisodes(text).FirstOrDefault();

    /// <summary>Every episode marker in the text, in order.</summary>
    public static IEnumerable<EpisodeMarker> ReadEpisodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in SeasonEpisode().Matches(text))
        {
            var first = Number(match, "episode");
            var last = match.Groups["last"].Success ? Number(match, "last") : first;
            yield return new EpisodeMarker(Number(match, "season"), first, Math.Max(first, last), match.Index, match.Length);
        }
    }

    /// <summary>The first whole-season marker ("S01", "Season 1") in the text, or null.</summary>
    public static (int Season, int Index)? ReadSeasonPack(string? text)
        => !string.IsNullOrEmpty(text) && SeasonPack().Match(text) is { Success: true } match
            ? (Number(match, "season"), match.Index)
            : null;

    /// <summary>The first daily date (2000.01.02 with any of . _ space - between the parts) in the text, in full, or null.</summary>
    public static (DateOnly Date, int Index)? ReadDailyDate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (Match match in DailyDate().Matches(text))
        {
            var year = Number(match, "year");
            var month = Number(match, "month");
            var day = Number(match, "day");
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                return (new DateOnly(year, month, day), match.Index);
            }
        }

        return null;
    }

    /// <summary>The first year (1900 to 2199) standing alone in the text, at or after <paramref name="start"/>, or null.</summary>
    public static (int Year, int Index)? ReadYear(string? text, int start = 0)
        => !string.IsNullOrEmpty(text) && start <= text.Length && Year().Match(text, start) is { Success: true } match
            ? (int.Parse(match.Value, CultureInfo.InvariantCulture), match.Index)
            : null;

    /// <summary>True when <paramref name="value"/> is a year Deluno reads: 1900 to 2199.</summary>
    public static bool IsYear(int value) => value is >= 1900 and <= 2199;

    private static int Number(Match match, string group)
        => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(StartsToken + "(?:" + SeasonEpisodePattern + "|" + NxNNPattern + ")" + EndsToken, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(StartsToken + "(?:" + SeasonPackPattern + ")" + EndsToken, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPack();

    [GeneratedRegex(@"(?<![0-9])(?<year>" + YearDigits + @")[ ._-](?<month>\d{2})[ ._-](?<day>\d{2})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DailyDate();

    [GeneratedRegex(YearToken, RegexOptions.CultureInvariant)]
    private static partial Regex Year();
}
