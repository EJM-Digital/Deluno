using System.Text.RegularExpressions;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Classifies a TV release name as a single episode, a multi-episode file, or
/// a season pack, from its own SxxEyy convention.
///
/// Moved here from <c>Deluno.Integrations.Search.SeasonPackDetector</c> (#776)
/// so the custom-format "releaseType" condition
/// (<see cref="CustomFormatDefinitionMatcher"/>) can share this exact
/// classification instead of re-implementing it: Deluno.Quality cannot
/// reference Deluno.Integrations, which itself references Deluno.Quality, so
/// the one shared copy has to live on this side. SeasonPackDetector keeps its
/// public API and now forwards to this type; nothing in Deluno.Integrations,
/// Deluno.Series or Deluno.Worker needed to change.
/// </summary>
public static partial class ReleaseEpisodeClassifier
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

    /// <summary>
    /// Sonarr's Release Type condition (#776): single episode, multi-episode
    /// or season pack. Null when the name carries none of those — Sonarr's
    /// own "Unknown" — so the condition never guesses.
    /// </summary>
    public static string? ReleaseType(string releaseName)
    {
        var classification = Classify(releaseName);
        if (classification.IsSeason)
        {
            return "season pack";
        }

        if (!classification.IsEpisode)
        {
            return null;
        }

        return classification.EpisodeEnd is not null && classification.EpisodeEnd != classification.Episode
            ? "multi-episode"
            : "single episode";
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
