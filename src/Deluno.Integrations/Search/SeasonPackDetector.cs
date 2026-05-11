using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search;

public static partial class SeasonPackDetector
{
    private static readonly Regex EpisodePattern = EpisodeRegex();
    private static readonly Regex SeasonOnlyPattern = SeasonOnlyRegex();
    private static readonly Regex MultiEpisodePattern = MultiEpisodeRegex();

    public static ReleaseEpisodeClassification Classify(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return new ReleaseEpisodeClassification(false, false, null, null, null);
        }

        var multiMatch = MultiEpisodePattern.Match(releaseName);
        if (multiMatch.Success)
        {
            var season = int.Parse(multiMatch.Groups["s"].Value);
            var epStart = int.Parse(multiMatch.Groups["e1"].Value);
            var epEnd = int.Parse(multiMatch.Groups["e2"].Value);
            return new ReleaseEpisodeClassification(
                IsSeason: false,
                IsEpisode: true,
                Season: season,
                Episode: epStart,
                EpisodeEnd: epEnd);
        }

        var episodeMatch = EpisodePattern.Match(releaseName);
        if (episodeMatch.Success)
        {
            return new ReleaseEpisodeClassification(
                IsSeason: false,
                IsEpisode: true,
                Season: int.Parse(episodeMatch.Groups["s"].Value),
                Episode: int.Parse(episodeMatch.Groups["e"].Value),
                EpisodeEnd: null);
        }

        var seasonMatch = SeasonOnlyPattern.Match(releaseName);
        if (seasonMatch.Success)
        {
            return new ReleaseEpisodeClassification(
                IsSeason: true,
                IsEpisode: false,
                Season: int.Parse(seasonMatch.Groups["s"].Value),
                Episode: null,
                EpisodeEnd: null);
        }

        return new ReleaseEpisodeClassification(false, false, null, null, null);
    }

    public static bool CoversEpisode(string releaseName, int season, int episode)
    {
        var classification = Classify(releaseName);
        if (classification.IsSeason && classification.Season == season)
        {
            return true;
        }

        if (!classification.IsEpisode || classification.Season != season)
        {
            return false;
        }

        var start = classification.Episode ?? episode;
        var end = classification.EpisodeEnd ?? start;
        return episode >= start && episode <= end;
    }

    [GeneratedRegex(@"[Ss](?<s>\d{1,2})[Ee](?<e1>\d{1,3})[Ee-](?<e2>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MultiEpisodeRegex();

    [GeneratedRegex(@"[Ss](?<s>\d{1,2})[Ee](?<e>\d{1,3})(?![Ee\d])", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"(?:^|[.\s])(?:Season[.\s]+)?[Ss](?<s>\d{1,2})(?:[.\s]|$)(?![Ee]\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonOnlyRegex();
}

public sealed record ReleaseEpisodeClassification(
    bool IsSeason,
    bool IsEpisode,
    int? Season,
    int? Episode,
    int? EpisodeEnd);
