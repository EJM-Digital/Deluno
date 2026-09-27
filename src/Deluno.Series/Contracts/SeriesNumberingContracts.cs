using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Deluno.Series.Contracts;

/// <summary>
/// The numbering vocabularies Deluno can reason about. The values are stored
/// as stable lower-case identifiers so a provider refresh cannot change the
/// meaning of an existing series.
/// </summary>
public static class SeriesTypes
{
    public const string Standard = "standard";
    public const string Daily = "daily";
    public const string Anime = "anime";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Standard, Daily, Anime };

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Daily => Daily,
            Anime => Anime,
            _ => Standard
        };

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim());
}

public static class SeriesNumberingSchemes
{
    public const string Standard = "standard";
    public const string AirDate = "airdate";
    public const string Absolute = "absolute";
    public const string Scene = "scene";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Standard, AirDate, Absolute, Scene };

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            AirDate => AirDate,
            Absolute => Absolute,
            Scene => Scene,
            _ => Standard
        };

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim());

    /// <summary>
    /// Returns the safe default implied by a series type when an older caller
    /// did not persist a separate scheme. Standard series keep the historical
    /// SxxEyy behaviour; Daily and Anime use their native date/absolute keys.
    /// </summary>
    public static string ForSeriesType(string? seriesType) =>
        SeriesTypes.Normalize(seriesType) switch
        {
            SeriesTypes.Daily => AirDate,
            SeriesTypes.Anime => Absolute,
            _ => Standard
        };

    public static string Resolve(string? seriesType, string? numberingScheme) =>
        string.IsNullOrWhiteSpace(numberingScheme)
            ? ForSeriesType(seriesType)
            : Normalize(numberingScheme);
}

/// <summary>
/// #763: how a file that spans more than one episode renders its combined
/// season/episode segment, mirroring Sonarr's own "Multi-Episode Style"
/// choices. Only the season/episode token portion of a naming template is
/// affected; {Episode Title} always joins every episode's title with " + ",
/// regardless of the style chosen here.
/// </summary>
public static class MultiEpisodeStyles
{
    public const string Extend = "extend";
    public const string Duplicate = "duplicate";
    public const string Repeat = "repeat";
    public const string Scene = "scene";
    public const string Range = "range";
    public const string PrefixedRange = "prefixed-range";

    public static readonly IReadOnlyList<string> All = [Extend, Duplicate, Repeat, Scene, Range, PrefixedRange];

    /// <summary>Range is Sonarr's own default.</summary>
    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Extend => Extend,
            Duplicate => Duplicate,
            Repeat => Repeat,
            Scene => Scene,
            PrefixedRange => PrefixedRange,
            _ => Range
        };

    /// <summary>
    /// The season/episode portion of a multi-episode file name -- e.g. for
    /// season 1, episodes 1-3: Extend "S01E01E02E03", Duplicate
    /// "S01E01S01E02S01E03", Repeat "S01E01-S01E02-S01E03", Scene
    /// "S01E01-E02-E03", Range "S01E01-E03", Prefixed Range "S01E01-03". A
    /// single episode always renders the same "S01E01" regardless of style.
    /// </summary>
    public static string FormatToken(string? style, int seasonNumber, IReadOnlyList<int> episodeNumbers)
    {
        var ordered = episodeNumbers.Distinct().OrderBy(number => number).ToArray();
        if (ordered.Length == 0)
        {
            return string.Empty;
        }

        var season = seasonNumber.ToString("D2", CultureInfo.InvariantCulture);
        if (ordered.Length == 1)
        {
            return $"S{season}E{ordered[0]:D2}";
        }

        return Normalize(style) switch
        {
            Extend => $"S{season}" + string.Concat(ordered.Select(episode => $"E{episode:D2}")),
            Duplicate => string.Concat(ordered.Select(episode => $"S{season}E{episode:D2}")),
            Repeat => string.Join("-", ordered.Select(episode => $"S{season}E{episode:D2}")),
            Scene => $"S{season}E{ordered[0]:D2}" + string.Concat(ordered.Skip(1).Select(episode => $"-E{episode:D2}")),
            PrefixedRange => $"S{season}E{ordered[0]:D2}-{ordered[^1]:D2}",
            _ => $"S{season}E{ordered[0]:D2}-E{ordered[^1]:D2}"
        };
    }
}

public static class SeriesNumberingSources
{
    public const string Provider = "provider";
    public const string Owner = "owner";

    /// <summary>
    /// An automatic scene/absolute mapping fetched from a third-party
    /// numbering source (TheXEM) rather than typed by the owner or read from
    /// the metadata provider's own season/episode list. Recorded separately so
    /// the System screen and the per-episode source label can say where a
    /// number actually came from, and so a provider catalogue refresh does not
    /// read as having invented it.
    /// </summary>
    public const string SceneMap = "scene-map";

    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, Owner, StringComparison.OrdinalIgnoreCase))
        {
            return Owner;
        }

        if (string.Equals(trimmed, SceneMap, StringComparison.OrdinalIgnoreCase))
        {
            return SceneMap;
        }

        return Provider;
    }
}

/// <summary>The persisted numbering choice for one series.</summary>
public sealed record SeriesNumberingDetail(
    string SeriesId,
    string SeriesType,
    string NumberingScheme,
    string NumberingSource,
    DateTimeOffset? UpdatedUtc,
    IReadOnlyList<SeriesEpisodeNumbering> Episodes);

/// <summary>
/// Alternate numbers attached to an episode. Canonical season/episode remains
/// the stable identity; these values are lookup keys, never replacement keys.
/// </summary>
public sealed record SeriesEpisodeNumbering(
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    int? AbsoluteNumber,
    int? SceneSeasonNumber,
    int? SceneEpisodeNumber,
    DateOnly? AirDate,
    string? NumberingSource,
    string? Title = null);

/// <summary>A user-owned alternate-number mapping submitted from the UI/API.</summary>
public sealed record SeriesNumberingMapping(
    string EpisodeId,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    DateOnly? AirDate = null);

/// <summary>
/// One episode's automatic scene/absolute mapping, keyed by the canonical
/// season/episode a third-party numbering source (TheXEM) reported it against
/// — unlike <see cref="SeriesNumberingMapping"/>, which the UI keys by episode
/// id because it already has the episode loaded. An automatic refresh only
/// ever knows season/episode, so it looks the episode up the same way a
/// provider catalogue sync does.
/// </summary>
public sealed record SeriesEpisodeSceneMapping(
    int SeasonNumber,
    int EpisodeNumber,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null);

public sealed record UpdateSeriesNumberingRequest(
    string? SeriesType = null,
    string? NumberingScheme = null,
    string? NumberingSource = null,
    IReadOnlyList<SeriesNumberingMapping>? Mappings = null);

/// <summary>One number parsed from a file name, before catalogue matching.</summary>
public sealed record ParsedSeriesEpisodeNumber(
    string NumberingScheme,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteNumber,
    int? SceneSeasonNumber,
    int? SceneEpisodeNumber,
    DateOnly? AirDate,
    string Token);

public sealed record SeriesNumberingParseResult(
    IReadOnlyList<ParsedSeriesEpisodeNumber> Matches,
    string? Warning = null)
{
    public bool IsAmbiguous => Matches.Count > 1;
}

/// <summary>
/// Safe filename parsing and alternate-number matching for TV imports.
/// Returning no match is intentional: a guessed episode is worse than a
/// recoverable unmatched import.
/// </summary>
public static partial class SeriesNumberingResolver
{
    /// <summary>
    /// Returns the season numbers named by season-pack tokens such as
    /// <c>S01</c> or <c>Season 01</c>. An episode token is deliberately not
    /// accepted here; a file named <c>S01E01</c> must be handled by the
    /// episode parser instead.
    /// </summary>
    public static IReadOnlyList<int> ParseSeasonPackNumbers(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return [];
        }

        // Keep the final dotted token intact. A season-pack source can be a
        // directory named "Show.S01", where treating ".S01" as an extension
        // would discard the only season identity. Real file extensions do not
        // interfere with the token boundary used by SeasonPackPattern.
        var leafName = Path.GetFileName(fileName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SeasonPackPattern()
            .Matches(leafName)
            .Select(match => int.TryParse(match.Groups["season"].Value, out var season) ? season : 0)
            .Where(season => season >= 0)
            .Distinct()
            .OrderBy(season => season)
            .ToArray();
    }

    public static SeriesNumberingParseResult ParseFileName(
        string? fileName,
        string? numberingScheme = SeriesNumberingSchemes.Standard)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new([], "The filename is empty.");
        }

        var scheme = SeriesNumberingSchemes.Normalize(numberingScheme);
        return scheme switch
        {
            SeriesNumberingSchemes.Standard => ParseStandard(fileName, scene: false),
            SeriesNumberingSchemes.Scene => ParseStandard(fileName, scene: true),
            SeriesNumberingSchemes.AirDate => ParseAirDate(fileName),
            SeriesNumberingSchemes.Absolute => ParseAbsolute(fileName),
            _ => new([], $"The numbering scheme '{numberingScheme}' is not supported.")
        };
    }

    /// <summary>
    /// Matches one parsed token against the provider catalogue. A match is
    /// returned only when exactly one canonical episode owns that key.
    /// </summary>
    public static bool TryResolve(
        ParsedSeriesEpisodeNumber parsed,
        IEnumerable<SeriesEpisodeNumbering> episodes,
        out SeriesEpisodeNumbering? match,
        out string? reason)
    {
        var candidates = episodes.Where(episode => parsed.NumberingScheme switch
        {
            SeriesNumberingSchemes.Standard =>
                parsed.SeasonNumber == episode.SeasonNumber && parsed.EpisodeNumber == episode.EpisodeNumber,
            SeriesNumberingSchemes.Scene =>
                parsed.SceneSeasonNumber == episode.SceneSeasonNumber &&
                parsed.SceneEpisodeNumber == episode.SceneEpisodeNumber,
            SeriesNumberingSchemes.Absolute =>
                parsed.AbsoluteNumber is not null && parsed.AbsoluteNumber == episode.AbsoluteNumber,
            SeriesNumberingSchemes.AirDate =>
                parsed.AirDate is not null && parsed.AirDate == episode.AirDate,
            _ => false
        }).ToArray();

        if (candidates.Length == 1)
        {
            match = candidates[0];
            reason = null;
            return true;
        }

        match = null;
        reason = candidates.Length == 0
            ? "No catalogued episode owns this numbering key."
            : "More than one catalogued episode owns this numbering key; the import was left unmatched.";
        return false;
    }

    private static SeriesNumberingParseResult ParseStandard(string fileName, bool scene)
    {
        var match = StandardPattern().Match(Path.GetFileNameWithoutExtension(fileName));
        if (!match.Success || !int.TryParse(match.Groups["season"].Value, out var season))
        {
            return new([], "No unambiguous season/episode token was found.");
        }

        var results = new List<ParsedSeriesEpisodeNumber>();
        foreach (Match episode in EpisodeTokenPattern().Matches(match.Groups["episodes"].Value))
        {
            if (!int.TryParse(episode.Groups["number"].Value, out var firstEpisode))
            {
                continue;
            }

            var lastEpisode = int.TryParse(episode.Groups["end"].Value, out var parsedEnd)
                ? parsedEnd
                : firstEpisode;
            if (lastEpisode < firstEpisode || lastEpisode - firstEpisode > 100)
            {
                continue;
            }

            for (var episodeNumber = firstEpisode; episodeNumber <= lastEpisode; episodeNumber++)
            {
                results.Add(scene
                    ? new(
                        SeriesNumberingSchemes.Scene,
                        null,
                        null,
                        null,
                        season,
                        episodeNumber,
                        null,
                        episode.Value)
                    : new(
                        SeriesNumberingSchemes.Standard,
                        season,
                        episodeNumber,
                        null,
                        null,
                        null,
                        null,
                        episode.Value));
            }
        }

        return results.Count == 0
            ? new([], "The season/episode token did not contain a usable episode number.")
            : new(results);
    }

    private static SeriesNumberingParseResult ParseAirDate(string fileName)
    {
        var match = AirDatePattern().Match(Path.GetFileNameWithoutExtension(fileName));
        if (!match.Success || !DateOnly.TryParseExact(
                match.Groups["date"].Value.Replace('.', '-').Replace('_', '-'),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var airDate))
        {
            return new([], "No unambiguous yyyy-MM-dd air-date token was found.");
        }

        return new([
            new(
                SeriesNumberingSchemes.AirDate,
                null,
                null,
                null,
                null,
                null,
                airDate,
                match.Value)
        ]);
    }

    private static SeriesNumberingParseResult ParseAbsolute(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var match = AbsoluteDashPattern().Match(name);
        if (!match.Success)
        {
            match = AbsoluteEpisodePattern().Match(name);
        }

        if (!match.Success || !int.TryParse(match.Groups["absolute"].Value, out var absolute) ||
            absolute is 0 or > 9999 || absolute is >= 480 and <= 2160 || absolute is >= 1900 and <= 2100)
        {
            return new([], "No unambiguous anime absolute episode token was found.");
        }

        return new([
            new(
                SeriesNumberingSchemes.Absolute,
                null,
                null,
                absolute,
                null,
                null,
                null,
                match.Value)
        ]);
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<season>\d{1,3})(?<episodes>E\d{1,3}(?:E\d{1,3}|-\s*E?\s*\d{1,3})*)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandardPattern();

    [GeneratedRegex(@"E(?<number>\d{1,3})(?:\s*-\s*E?\s*(?<end>\d{1,3}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:Season[.\s_-]+|S)(?<season>\d{1,3})(?!E\d{1,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"(?<!\d)(?<date>20\d{2}[._-]\d{2}[._-]\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex AirDatePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])-\s*(?<absolute>\d{1,4})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteDashPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:ep(?:isode)?[ ._-]*)?(?<absolute>\d{1,4})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteEpisodePattern();
}
