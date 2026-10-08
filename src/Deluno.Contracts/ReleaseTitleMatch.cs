using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>What a search was for, to check a release's name against (#613).</summary>
public sealed record ReleaseTitleExpectation(
    string Title,
    int? Year,
    bool IsTv,
    int? Season = null,
    int? Episode = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    IReadOnlyList<string>? OtherTitles = null,
    /// <summary>
    /// The provider's titles for the episodes of <see cref="Season"/>, by episode number (#1173). A release whose
    /// name carries another episode's title is refused for this one.
    /// </summary>
    IReadOnlyDictionary<int, string>? EpisodeTitles = null,
    /// <summary>
    /// The provider's episode titles for every season, by season then episode number, for a search of the whole show
    /// that has no target episode: a release is then checked against its own SxxEyy (#1173).
    /// </summary>
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? SeasonEpisodeTitles = null);

/// <summary>
/// Whether a release's name is for the title searched (#613).
///
/// <para>Deluno trusted the indexer to have matched. A Newznab indexer
/// searched by id mostly has; a tracker searched by words need not have, and
/// Nyaa answered "Say Yes to the Dress S01E09" with "Kirio Fan Club S01E09
/// Can't Say It, Won't Say It", which Deluno grabbed. Radarr and Sonarr read
/// the title, year and episode out of the name and refuse what is for
/// something else; so does this.</para>
///
/// <para>The title is the name up to where it stops being a title: a year, an
/// episode or season marker, a quality or source word, a bracket. A title can
/// itself hold a year ("Blade Runner 2049"), so every such point is tried and
/// any that leaves the title searched for is enough. A name giving several
/// titles ("Ночь живых мертвецов / Night of the Living Dead") matches on any
/// of them.</para>
/// </summary>
public static partial class ReleaseTitleMatch
{
    /// <summary>Null when the name is for what was searched; otherwise why it is not, in words.</summary>
    public static string? Mismatch(string releaseName, ReleaseTitleExpectation expected)
    {
        if (string.IsNullOrWhiteSpace(releaseName) || string.IsNullOrWhiteSpace(expected.Title))
        {
            return null;
        }

        var name = Extension().Replace(LeadingTags().Replace(releaseName.Trim(), string.Empty), string.Empty);
        var wanted = new[] { expected.Title }.Concat(expected.OtherTitles ?? [])
            .Select(Clean)
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        int? matchedCut = null;
        foreach (var cut in CutPoints(name))
        {
            var prefix = name[..cut];
            if (Segments(prefix).Any(segment => wanted.Contains(Clean(segment))))
            {
                matchedCut = cut;
                break;
            }
        }

        if (matchedCut is null)
        {
            return $"This release is for something else: its name does not give the title \"{expected.Title}\".";
        }

        var rest = name[matchedCut.Value..];
        return expected.IsTv
            ? SeriesYearMismatch(rest, expected) ?? EpisodeMismatch(rest, expected)
            : YearMismatch(rest, expected);
    }

    /// <summary>
    /// True when the name carries the title of the episode asked for (#1173), so a search can prefer it over a
    /// release that names no title at all.
    /// </summary>
    public static bool CarriesWantedEpisodeTitle(string releaseName, ReleaseTitleExpectation expected)
    {
        if (!expected.IsTv || expected.Episode is not { } wantedEpisode
            || expected.EpisodeTitles is null || !expected.EpisodeTitles.TryGetValue(wantedEpisode, out var wantedTitle))
        {
            return false;
        }

        var name = Extension().Replace(LeadingTags().Replace(releaseName.Trim(), string.Empty), string.Empty);
        var marker = SeasonEpisode().Match(name);
        return marker.Success && TitleAfterMarker(name[(marker.Index + marker.Length)..]) is { Length: > 0 } carried
            && TitlesAgree(carried, Clean(wantedTitle));
    }

    /// <summary>True when the name carries a year equal (within a year) to the series' own first-air year (#1166).</summary>
    public static bool CarriesSeriesYear(string releaseName, ReleaseTitleExpectation expected)
    {
        if (expected.Year is not { } year)
        {
            return false;
        }

        var name = Extension().Replace(LeadingTags().Replace(releaseName.Trim(), string.Empty), string.Empty);
        return NameYearBeforeEpisode(name) is { } found && Math.Abs(found - year) <= 1;
    }

    /// <summary>
    /// A year in the name, ahead of the season and episode marker, that is not the series' own year (#1166): The
    /// Twilight Zone 2019 is not the 1959 show. A date-named daily release ("Show 2024 05 01") is not a year.
    /// </summary>
    private static string? SeriesYearMismatch(string rest, ReleaseTitleExpectation expected)
    {
        if (expected.Year is not { } year || expected.AirDate is not null)
        {
            return null;
        }

        if (NameYearBeforeEpisode(rest) is not { } releaseYear || Math.Abs(releaseYear - year) <= 1)
        {
            return null;
        }

        return $"This release is for another show: {expected.Title} ({releaseYear}), not ({year}).";
    }

    private static int? NameYearBeforeEpisode(string text)
    {
        var marker = SeasonEpisode().Match(text);
        var pack = SeasonPack().Match(text);
        var end = marker.Success ? marker.Index : pack.Success ? pack.Index : -1;
        if (end < 0)
        {
            return null;
        }

        var found = Year().Match(text[..end]);
        if (!found.Success)
        {
            return null;
        }

        // 2024.05.01 is an air date, not a year.
        return DateTail().IsMatch(text[found.Index..end]) ? null : int.Parse(found.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The words between the episode marker and the first quality/source word, cleaned for comparison.</summary>
    private static string TitleAfterMarker(string afterMarker)
    {
        var cut = Boundary().Match(afterMarker);
        return Clean(cut.Success ? afterMarker[..cut.Index] : afterMarker);
    }

    private static bool TitlesAgree(string carried, string other)
        => other.Length > 0
           && (carried.StartsWith(other, StringComparison.Ordinal)
               || (carried.Length >= 4 && other.StartsWith(carried, StringComparison.Ordinal)));

    /// <summary>
    /// The name says one episode but carries another's title (#1173): "S38E02 The Childrens Book Job" when that is
    /// E01's. Punctuation, apostrophes, "&amp;" and articles do not count (<see cref="Clean"/>). A name with no title,
    /// or one matching nothing the provider lists, is not refused here.
    /// </summary>
    public static string? EpisodeTitleMismatch(string afterMarker, int season, int episode, ReleaseTitleExpectation expected)
    {
        if (expected.EpisodeTitles is not { Count: > 0 } titles)
        {
            return null;
        }

        var carried = TitleAfterMarker(afterMarker);
        if (carried.Length == 0)
        {
            return null;
        }

        if (titles.TryGetValue(episode, out var wanted) && TitlesAgree(carried, Clean(wanted)))
        {
            return null;
        }

        foreach (var (number, title) in titles)
        {
            if (number == episode || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var other = Clean(title);
            if (other.Length >= 3 && TitlesAgree(carried, other))
            {
                return $"The name says S{season:D2}E{episode:D2}, but its title is S{season:D2}E{number:D2}'s \"{title.Trim()}\".";
            }
        }

        return null;
    }

    /// <summary>
    /// The title and year a release name gives, as Radarr reads them: the name
    /// up to its first year, episode marker, quality word or bracket (#617).
    /// "The Kid 1921 CC 1080p BluRay x264-nikt0" is The Kid, 1921 - not a film
    /// called all of that.
    /// </summary>
    public static (string Title, int? Year) ParseTitleAndYear(string releaseName)
    {
        var name = Extension().Replace(LeadingTags().Replace(releaseName.Trim(), string.Empty), string.Empty);
        var cut = CutPoints(name).First();
        var title = TitleSeparators().Replace(name[..cut], " ").Trim(' ', '-', '(', '[');
        // The first year after the title: "The Kid 1921", or "House on Haunted
        // Hill (1959)" as a tracker lists it.
        var year = Year().Match(name, cut) is { Success: true } found
            ? int.Parse(found.Value, CultureInfo.InvariantCulture)
            : (int?)null;
        return (title.Length > 0 ? title : releaseName, year);
    }

    /// <summary>
    /// Case, accents, punctuation, "&amp;" for "and", and the small words a
    /// release leaves out or puts in ("The Kid" is "Kid"), as Sonarr cleans a title.
    /// </summary>
    public static string Clean(string value)
    {
        var decomposed = value.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '`')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("a" or "an" or "the" or "and" or "or" or "of"))
            .ToArray();
        return string.Concat(words);
    }

    private static IEnumerable<int> CutPoints(string name)
    {
        var cuts = new SortedSet<int>();
        foreach (Match match in Boundary().Matches(name))
        {
            if (match.Index > 0)
            {
                cuts.Add(match.Index);
            }
        }

        cuts.Add(name.Length);
        return cuts;
    }

    private static IEnumerable<string> Segments(string prefix)
        => prefix.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => AkaSplit().Split(part))
            .Append(prefix);

    private static string? YearMismatch(string rest, ReleaseTitleExpectation expected)
    {
        if (expected.Year is not { } year)
        {
            return null;
        }

        var found = Year().Match(rest);
        if (!found.Success)
        {
            return null;
        }

        var releaseYear = int.Parse(found.Value, CultureInfo.InvariantCulture);
        return Math.Abs(releaseYear - year) <= 1
            ? null
            : $"This release is from {releaseYear}; {expected.Title} is from {year}.";
    }

    /// <summary>
    /// A search of the whole show has no target episode, but a release still says which one it is (SxxEyy) and may
    /// carry that episode's title; the two are compared with the provider's titles for that season (#1173).
    /// </summary>
    private static string? WholeShowTitleMismatch(string rest, ReleaseTitleExpectation expected)
    {
        if (expected.SeasonEpisodeTitles is not { Count: > 0 } bySeason)
        {
            return null;
        }

        var marker = SeasonEpisode().Match(rest);
        if (!marker.Success || marker.Groups["last"].Success)
        {
            return null;
        }

        var season = int.Parse(marker.Groups["season"].Value, CultureInfo.InvariantCulture);
        var episode = int.Parse(marker.Groups["episode"].Value, CultureInfo.InvariantCulture);
        return bySeason.TryGetValue(season, out var titles)
            ? EpisodeTitleMismatch(rest[(marker.Index + marker.Length)..], season, episode, expected with { EpisodeTitles = titles })
            : null;
    }

    private static string? EpisodeMismatch(string rest, ReleaseTitleExpectation expected)
    {
        if (expected.Season is null && expected.Episode is null && expected.AbsoluteNumber is null && expected.AirDate is null)
        {
            return WholeShowTitleMismatch(rest, expected);
        }

        var episode = SeasonEpisode().Match(rest);
        if (episode.Success)
        {
            var season = int.Parse(episode.Groups["season"].Value, CultureInfo.InvariantCulture);
            var first = int.Parse(episode.Groups["episode"].Value, CultureInfo.InvariantCulture);
            var last = episode.Groups["last"].Success ? int.Parse(episode.Groups["last"].Value, CultureInfo.InvariantCulture) : first;
            if (expected.Season is { } wantedSeason && season != wantedSeason)
            {
                return $"This release is season {season}; the search was for season {wantedSeason}.";
            }

            if (expected.Episode is { } wantedEpisode && (wantedEpisode < first || wantedEpisode > last))
            {
                return first == last
                    ? $"This release is episode {first}; the search was for episode {wantedEpisode}."
                    : $"This release is episodes {first}-{last}; the search was for episode {wantedEpisode}.";
            }

            return first == last && expected.Episode is not null
                ? EpisodeTitleMismatch(rest[(episode.Index + episode.Length)..], season, first, expected)
                : null;
        }

        var pack = SeasonPack().Match(rest);
        if (pack.Success)
        {
            var season = int.Parse(pack.Groups["season"].Value, CultureInfo.InvariantCulture);
            if (expected.Season is { } wantedSeason && season != wantedSeason)
            {
                return $"This release is season {season}; the search was for season {wantedSeason}.";
            }

            return expected.Episode is not null
                ? $"This is a whole-season pack; the search was for one episode."
                : null;
        }

        if (expected.AirDate is { } airDate && rest.Contains(airDate.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return null;
        }

        if (expected.AbsoluteNumber is { } absolute && Absolute().Match(rest) is { Success: true } numbered)
        {
            return int.Parse(numbered.Groups["number"].Value, CultureInfo.InvariantCulture) == absolute
                ? null
                : $"This release is episode {numbered.Groups["number"].Value}; the search was for episode {absolute}.";
        }

        // A name that does not say which episode it is cannot be shown to be the right one.
        return expected.Episode is not null || expected.AbsoluteNumber is not null
            ? "The name does not say which episode this is."
            : null;
    }

    // Where a title ends in a release name.
    [GeneratedRegex(@"(?<![0-9])(19|20)\d{2}(?![0-9])|[\(\[]|\bS\d{1,2}(E\d{1,3})?\b|\b\d{1,2}x\d{2,3}\b|\b(season|series|complete|temporada|saison)\b|\s-\s\d{1,4}\b|(?<![a-z0-9])(480|576|720|1080|2160)[pi]\b|\b(4k|uhd|bluray|blu-ray|bdrip|brrip|bd|web|web-?dl|webrip|hdtv|hdrip|dvdrip|dvd|remux|x264|x265|h\.?264|h\.?265|hevc|xvid|proper|repack|remastered|extended|unrated|internal|multi|dual)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Boundary();

    [GeneratedRegex(@"^\s*(?:(?:\[[^\]]*\]|【[^】]*】)\s*)+")]
    private static partial Regex LeadingTags();

    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts|wmv)$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();

    [GeneratedRegex(@"\s+a\.?k\.?a\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AkaSplit();

    [GeneratedRegex(@"(?<![0-9])(19|20)\d{2}(?![0-9])")]
    private static partial Regex Year();

    // A dash stuck to a separator is one too ("house.on.-haunted"); a hyphen
    // inside a word ("Spider-Man") is not.
    [GeneratedRegex(@"^(?:19|20)\d{2}[ ._-]\d{2}[ ._-]\d{2}")]
    private static partial Regex DateTail();

    [GeneratedRegex(@"[._\s]+(?:-[._\s]*)?")]
    private static partial Regex TitleSeparators();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[ ._-]?E(?<episode>\d{1,3})(?:-?E(?<last>\d{1,3})|-(?<last>\d{1,3}))?\b|\b(?<season>\d{1,2})x(?<episode>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})\b|\b(?:season|series|saison|temporada)[ ._-]?(?<season>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPack();

    [GeneratedRegex(@"(?:\s-\s|\bE|#)(?<number>\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Absolute();
}
