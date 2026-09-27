using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

/// <summary>
/// Forwards to <see cref="ReleaseEpisodeClassifier"/> (Deluno.Quality), which
/// holds the actual classification so the custom-format "releaseType"
/// condition (#776) can share it instead of duplicating these regexes. Kept
/// as a thin wrapper under its original name and namespace so every existing
/// caller in Deluno.Integrations, Deluno.Series and Deluno.Worker is unchanged.
/// </summary>
public static class SeasonPackDetector
{
    public static ReleaseEpisodeClassification Classify(string releaseName)
        => ReleaseEpisodeClassifier.Classify(releaseName);

    public static bool CoversEpisode(string releaseName, int season, int episode)
        => ReleaseEpisodeClassifier.CoversEpisode(releaseName, season, episode);
}
