using Deluno.Series.Contracts;

namespace Deluno.Retention;

/// <summary>
/// What Deluno works out from one show's episode list (#1004, #1005), shared by
/// every field that needs it so no two of them disagree about what "newest" or
/// "finale" means. Sonarr's own answers come from its rank maps and its finale
/// flags; Deluno has the episode list, so it derives the same things from it.
///
/// <para><b>Unknown stays unknown.</b> Every answer that cannot be given
/// (an undated episode, a season that has not finished airing) is null, and a
/// null never matches a rule.</para>
/// </summary>
public sealed class RetentionEpisodeMath
{
    private readonly IReadOnlyList<SeriesEpisodeInventoryItem> _episodes;
    private readonly DateTimeOffset _now;
    private readonly Dictionary<string, int> _rankByEpisode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _rankBySeason = [];

    public RetentionEpisodeMath(IReadOnlyList<SeriesEpisodeInventoryItem> episodes, DateTimeOffset now)
    {
        _episodes = episodes;
        _now = now;

        // Newest first among the aired episodes on disk, specials left out
        // (Sonarr's episodeFileRank pool). Same air date: later season, then later episode.
        var pool = episodes
            .Where(episode => episode.HasFile && episode.SeasonNumber > 0 && episode.AirDateUtc is { } aired && aired <= now)
            .OrderByDescending(episode => episode.AirDateUtc)
            .ThenByDescending(episode => episode.SeasonNumber)
            .ThenByDescending(episode => episode.EpisodeNumber)
            .ToArray();
        for (var index = 0; index < pool.Length; index++)
        {
            _rankByEpisode[pool[index].EpisodeId] = index + 1;
            // A season ranks in the order it first appears in the newest-first pool:
            // by the air date of its newest episode on disk.
            if (!_rankBySeason.ContainsKey(pool[index].SeasonNumber))
            {
                _rankBySeason[pool[index].SeasonNumber] = _rankBySeason.Count + 1;
            }
        }
    }

    /// <summary>1 is the newest episode on disk; null when it is not in the pool (a special, unaired, undated or without a file).</summary>
    public int? EpisodeRank(string episodeId) => _rankByEpisode.TryGetValue(episodeId, out var rank) ? rank : null;

    /// <summary>1 is the season with the newest episode on disk; null for specials and for a season with nothing aired on disk.</summary>
    public int? SeasonRank(int season) => _rankBySeason.TryGetValue(season, out var rank) ? rank : null;

    /// <summary>
    /// Monitored episodes that have aired and have no file. Specials only count in a season of their own.
    /// An episode with no air date counts as Missing; only an episode with a future air date is Upcoming (James, 30 Sep, #1072).
    /// </summary>
    public int? Missing(int? season)
        => _episodes.Count(episode => (season is null ? episode.SeasonNumber > 0 : episode.SeasonNumber == season) && episode.Monitored && !episode.HasFile && !EpisodeUpcoming(episode, _now));

    /// <summary>
    /// Upcoming, the word movies use: an episode with a future air date. An episode with no air date is Missing, not
    /// Upcoming. Retention never acts on anything Upcoming.
    /// </summary>
    public static bool EpisodeUpcoming(SeriesEpisodeInventoryItem episode, DateTimeOffset now)
        => episode.AirDateUtc is { } aired && aired > now;

    /// <summary>A season every one of whose episodes has a future air date.</summary>
    public bool SeasonUpcoming(int season)
    {
        var inSeason = _episodes.Where(episode => episode.SeasonNumber == season).ToArray();
        return inSeason.Length > 0 && inSeason.All(episode => EpisodeUpcoming(episode, _now));
    }

    /// <summary>A show every one of whose episodes has a future air date.</summary>
    public bool ShowUpcoming()
        => _episodes.Count > 0 && _episodes.All(episode => EpisodeUpcoming(episode, _now));

    /// <summary>Whether any episode of the season is monitored; null when Deluno lists none.</summary>
    public bool? SeasonMonitored(int season)
    {
        var inSeason = _episodes.Where(episode => episode.SeasonNumber == season).ToArray();
        return inSeason.Length == 0 ? null : inSeason.Any(episode => episode.Monitored);
    }

    /// <summary>
    /// Still to come, for retention's "still airing" protection: a future air date, or no air date and no file yet (a season
    /// announced with dates to be announced). An undated episode that already has a file is not to come. This is separate from the
    /// rung, where an undated episode is Missing (#1072).
    /// </summary>
    public static bool StillToCome(SeriesEpisodeInventoryItem episode, DateTimeOffset now)
        => EpisodeUpcoming(episode, now) || (episode.AirDateUtc is null && !episode.HasFile);

    private bool HasToCome(int season)
        => _episodes.Any(episode => episode.SeasonNumber == season && StillToCome(episode, _now));

    private SeriesEpisodeInventoryItem? LastOf(int season)
        => _episodes.Where(episode => episode.SeasonNumber == season).OrderByDescending(episode => episode.EpisodeNumber).FirstOrDefault();

    private int? LastSeason()
        => _episodes.Where(episode => episode.SeasonNumber > 0).Select(episode => (int?)episode.SeasonNumber).Max();

    /// <summary>
    /// A season's last episode is its finale. True when that episode is on disk;
    /// false when it is not there (or the episode asked about is another one);
    /// null while the season still has episodes to come, because the last one
    /// listed is then not known to be the last.
    /// </summary>
    /// <param name="episodeId">Null asks about the whole season.</param>
    public bool? SeasonFinale(int season, string? episodeId)
    {
        if (season <= 0 || LastOf(season) is not { } last)
        {
            return false;
        }

        if (episodeId is not null && !string.Equals(last.EpisodeId, episodeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!last.HasFile)
        {
            return false;
        }

        return HasToCome(season) ? null : true;
    }

    /// <summary>
    /// The last episode of the last season is the series finale, once the show
    /// has ended. <paramref name="ended"/> is true for ended or cancelled, false
    /// for a show still running, null when Deluno does not know.
    /// </summary>
    /// <param name="season">Null asks about the whole show.</param>
    /// <param name="episodeId">Null asks about the season or the whole show.</param>
    public bool? SeriesFinale(int? season, string? episodeId, bool? ended)
    {
        var lastSeason = LastSeason();
        if (lastSeason is null)
        {
            return false;
        }

        var last = LastOf(lastSeason.Value)!;
        if (season is not null && season != lastSeason)
        {
            return false;
        }

        if (episodeId is not null && !string.Equals(last.EpisodeId, episodeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!last.HasFile || ended == false)
        {
            return false;
        }

        return ended is null || HasToCome(lastSeason.Value) ? null : true;
    }
}
