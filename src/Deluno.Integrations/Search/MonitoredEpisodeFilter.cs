namespace Deluno.Integrations.Search;

/// <summary>
/// Filters search candidates to those that cover at least one monitored, wanted episode.
/// Used when searching for a whole season so we don't auto-grab a pack if no episodes are monitored.
/// </summary>
public static class MonitoredEpisodeFilter
{
    public sealed record WantedEpisode(int SeasonNumber, int EpisodeNumber);

    /// <summary>
    /// Returns true if the release covers at least one episode in <paramref name="wantedEpisodes"/>.
    /// </summary>
    public static bool ReleaseCoversManagedEpisodes(
        string releaseName,
        IReadOnlyList<WantedEpisode> wantedEpisodes)
    {
        if (wantedEpisodes.Count == 0) return false;

        var classification = SeasonPackDetector.Classify(releaseName);

        if (classification.IsSeason)
        {
            return wantedEpisodes.Any(ep => ep.SeasonNumber == classification.Season);
        }

        if (classification.IsEpisode)
        {
            return wantedEpisodes.Any(ep =>
                ep.SeasonNumber == classification.Season &&
                SeasonPackDetector.CoversEpisode(releaseName, ep.SeasonNumber, ep.EpisodeNumber));
        }

        return true;
    }

    /// <summary>
    /// Filters candidates to those covering at least one wanted episode.
    /// Passes through all candidates when <paramref name="wantedEpisodes"/> is empty (non-episode search).
    /// </summary>
    public static IReadOnlyList<MediaSearchCandidate> Filter(
        IReadOnlyList<MediaSearchCandidate> candidates,
        IReadOnlyList<WantedEpisode> wantedEpisodes)
    {
        if (wantedEpisodes.Count == 0) return candidates;

        return candidates
            .Where(c => ReleaseCoversManagedEpisodes(c.ReleaseName, wantedEpisodes))
            .ToArray();
    }
}
