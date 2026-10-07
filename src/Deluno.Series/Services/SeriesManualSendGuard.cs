using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Media;
using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

/// <summary>
/// #1175: which download already on its way is for the same episode(s) as a release a person is
/// about to send by hand. Season and episode tokens in the release names decide it, so a pack
/// and one of its episodes count as the same thing, and a different episode never does.
/// </summary>
public static class SeriesManualSendGuard
{
    public static ActiveDownloadCheck Build(
        string seriesId,
        string seriesTitle,
        IReadOnlyList<(string EpisodeId, int SeasonNumber, int EpisodeNumber)> episodes,
        string releaseName,
        IDownloadClientTelemetryService telemetry)
    {
        var wanted = Coverage.From(releaseName);
        var byEpisodeId = episodes.ToDictionary(item => item.EpisodeId, StringComparer.OrdinalIgnoreCase);

        bool IsSameTarget(DownloadInFlight download)
        {
            if (string.Equals(download.EntityType, "episode", StringComparison.OrdinalIgnoreCase))
            {
                return byEpisodeId.TryGetValue(download.EntityId, out var episode) && wanted.Covers(episode.SeasonNumber, episode.EpisodeNumber);
            }

            if (!string.Equals(download.EntityId, seriesId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var other = Coverage.From(download.ReleaseName);
            return wanted.IsEmpty || other.IsEmpty
                ? string.Equals(download.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase)
                : wanted.Overlaps(other);
        }


        return new ActiveDownloadCheck(
            wanted.Describe(seriesTitle),
            IsSameTarget,
            (conflict, cancellationToken) => ActiveDownloadChecks.ReplaceAsync(telemetry, conflict, cancellationToken));
    }

    private sealed class Coverage
    {
        private readonly HashSet<int> seasons = [];
        private readonly HashSet<(int Season, int Episode)> episodes = [];

        public bool IsEmpty => seasons.Count == 0 && episodes.Count == 0;

        public static Coverage From(string? releaseName)
        {
            var coverage = new Coverage();
            if (string.IsNullOrWhiteSpace(releaseName))
            {
                return coverage;
            }

            // A trailing ".S01E02" would be read as a file extension, so give it a real one.
            foreach (var match in SeriesNumberingResolver.ParseFileName(releaseName + ".mkv", SeriesNumberingSchemes.Standard).Matches)
            {
                if (match.SeasonNumber is int season && match.EpisodeNumber is int episode)
                {
                    coverage.episodes.Add((season, episode));
                }
            }

            if (coverage.episodes.Count == 0)
            {
                foreach (var season in SeriesNumberingResolver.ParseSeasonPackNumbers(releaseName))
                {
                    coverage.seasons.Add(season);
                }
            }

            return coverage;
        }

        public bool Covers(int season, int episode) => seasons.Contains(season) || episodes.Contains((season, episode));

        public bool Overlaps(Coverage other)
            => seasons.Overlaps(other.seasons)
               || other.episodes.Any(item => Covers(item.Season, item.Episode))
               || episodes.Any(item => other.Covers(item.Season, item.Episode));

        public string Describe(string seriesTitle)
        {
            if (episodes.Count > 0)
            {
                return string.Join(", ", episodes.OrderBy(item => item.Season).ThenBy(item => item.Episode)
                    .Select(item => $"S{item.Season:00}E{item.Episode:00}"));
            }

            return seasons.Count > 0
                ? string.Join(", ", seasons.Order().Select(season => season == 0 ? "Specials" : $"Season {season}"))
                : seriesTitle;
        }
    }
}
