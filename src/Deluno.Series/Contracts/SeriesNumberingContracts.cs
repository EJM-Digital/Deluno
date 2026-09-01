using System.Globalization;
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

public static class SeriesNumberingSources
{
    public const string Provider = "provider";
    public const string Owner = "owner";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Owner, StringComparison.OrdinalIgnoreCase)
            ? Owner
            : Provider;
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
