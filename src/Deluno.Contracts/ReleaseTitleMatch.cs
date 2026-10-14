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
        var marker = ReleaseNameReader.ReadEpisode(name);
        return marker is not null && TitleAfterMarker(name[(marker.Index + marker.Length)..]) is { Length: > 0 } carried
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
        var marker = ReleaseNameReader.ReadEpisode(text);
        var pack = ReleaseNameReader.ReadSeasonPack(text);
        var end = marker is not null ? marker.Index : pack is { } p ? p.Index : -1;
        if (end < 0)
        {
            return null;
        }

        var before = text[..end];
        if (ReleaseNameReader.ReadYear(before) is not { } found)
        {
            return null;
        }

        // 2024.05.01 is an air date, not a year.
        return ReleaseNameReader.ReadDailyDate(before[found.Index..]) is { Index: 0 } ? null : found.Year;
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

        return OtherEpisodeTitleCarried(carried, episode, titles) is { } other
            ? $"The name says S{season:D2}E{episode:D2}, but its title is S{season:D2}E{other.Number:D2}'s \"{other.Title}\"."
            : null;
    }

    /// <summary>The other episode (not <paramref name="episode"/>) whose exact title the carried words are, or null.</summary>
    private static (int Number, string Title)? OtherEpisodeTitleCarried(string carried, int episode, IReadOnlyDictionary<int, string> titles)
    {
        foreach (var (number, title) in titles)
        {
            if (number == episode || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var other = Clean(title);
            if (other.Length >= 3 && TitlesAgree(carried, other))
            {
                return (number, title.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// An already-imported file's source name that says one episode but carries another's exact title (#1173): the same
    /// rule <see cref="Mismatch"/> applies to a search, returned as the episode it really is. Null when the name is
    /// for the show and episode in <paramref name="expected"/>, carries that episode's own title, or carries no title.
    /// </summary>
    public static (int OtherEpisode, string OtherTitle)? MislabelledAs(string releaseName, ReleaseTitleExpectation expected)
    {
        if (!expected.IsTv || expected.Episode is not { } episode || expected.EpisodeTitles is not { Count: > 0 } titles
            || Mismatch(releaseName, expected) is not { } reason
            || !reason.Contains("but its title is", StringComparison.Ordinal))
        {
            return null;
        }

        var name = Extension().Replace(LeadingTags().Replace(releaseName.Trim(), string.Empty), string.Empty);
        var marker = ReleaseNameReader.ReadEpisode(name);
        if (marker is null)
        {
            return null;
        }

        var carried = TitleAfterMarker(name[(marker.Index + marker.Length)..]);
        return carried.Length > 0 && OtherEpisodeTitleCarried(carried, episode, titles) is { } other
            ? (other.Number, other.Title)
            : null;
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
        var year = ReleaseNameReader.ReadYear(name, cut)?.Year;
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

        if (ReleaseNameReader.ReadYear(rest) is not { } found)
        {
            return null;
        }

        var releaseYear = found.Year;
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

        var marker = ReleaseNameReader.ReadEpisode(rest);
        if (marker is null || marker.LastEpisode != marker.FirstEpisode)
        {
            return null;
        }

        return bySeason.TryGetValue(marker.Season, out var titles)
            ? EpisodeTitleMismatch(rest[(marker.Index + marker.Length)..], marker.Season, marker.FirstEpisode, expected with { EpisodeTitles = titles })
            : null;
    }

    private static string? EpisodeMismatch(string rest, ReleaseTitleExpectation expected)
    {
        if (expected.Season is null && expected.Episode is null && expected.AbsoluteNumber is null && expected.AirDate is null)
        {
            return WholeShowTitleMismatch(rest, expected);
        }

        if (ReleaseNameReader.ReadEpisode(rest) is { } episode)
        {
            var season = episode.Season;
            var first = episode.FirstEpisode;
            var last = episode.LastEpisode;
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

        if (ReleaseNameReader.ReadSeasonPack(rest) is { } pack)
        {
            var season = pack.Season;
            if (expected.Season is { } wantedSeason && season != wantedSeason)
            {
                return $"This release is season {season}; the search was for season {wantedSeason}.";
            }

            return expected.Episode is not null
                ? $"This is a whole-season pack; the search was for one episode."
                : null;
        }

        // A daily name carries its whole date, and only that date is the episode asked for.
        if (expected.AirDate is { } airDate && ReleaseNameReader.ReadDailyDate(rest) is { } daily)
        {
            return daily.Date == airDate
                ? null
                : $"This release is dated {daily.Date:yyyy-MM-dd}; the search was for {airDate:yyyy-MM-dd}.";
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
    [GeneratedRegex(@"" + ReleaseNameReader.YearToken + @"|[\(\[]|" + ReleaseNameReader.StartsToken + @"(?:S\d{1,2}(?:[ ._-]?E\d{1,3})*|\d{1,2}x\d{2,3}|season|series|complete|temporada|saison)" + ReleaseNameReader.EndsToken + @"|\s-\s\d{1,4}\b|" + ReleaseNameReader.StartsToken + @"(?:(?:480|576|720|1080|2160)[pi]|4k|uhd|bluray|blu-ray|bdrip|brrip|bd|web|web-?dl|webrip|hdtv|hdrip|dvdrip|dvd|remux|x264|x265|h\.?264|h\.?265|hevc|xvid|proper|repack|remastered|extended|unrated|internal|multi|dual)" + ReleaseNameReader.EndsToken, RegexOptions.IgnoreCase)]
    private static partial Regex Boundary();

    [GeneratedRegex(@"^\s*(?:(?:\[[^\]]*\]|【[^】]*】)\s*)+")]
    private static partial Regex LeadingTags();

    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts|wmv)$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();

    [GeneratedRegex(@"\s+a\.?k\.?a\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AkaSplit();

    [GeneratedRegex(@"[._\s]+(?:-[._\s]*)?")]
    private static partial Regex TitleSeparators();

    [GeneratedRegex(@"(?:\s-\s|\bE|#)(?<number>\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Absolute();
}
