using Deluno.Jobs.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

/// <summary>
/// Decides which of the automatic cycle's missing episodes should be searched
/// together, as one season, rather than one at a time (#646).
///
/// <para>A season pack only ever wins when the evidence is unambiguous: every
/// monitored episode of the season has aired, all of them are missing, and
/// there are at least two of them (one missing episode is not a "season" — it
/// is the per-episode path already handles). An episode with no air date on
/// record holds the whole season to the per-episode path, the conservative
/// reading, because a pack grab is judged and dispatched as one release and
/// cannot leave part of a season for later the way per-episode search
/// naturally does.</para>
///
/// <para>A season is only planned for the episodes that are both missing
/// <b>and</b> already due for their own search — an episode still inside its
/// retry window is left out of both the pack and the per-episode plan, so a
/// pack search never runs ahead of the window that paces routine searching.
/// When that leaves the season short of "every monitored aired episode is
/// missing", the season falls back to the ordinary per-episode plan for
/// whichever of its episodes are actually due.</para>
/// </summary>
public static class SeasonSearchPlanner
{
    public const int MinimumSeasonSize = 2;

    public sealed record Plan(
        IReadOnlyList<SeasonSearchPlanItem> SeasonSearches,
        IReadOnlyList<EpisodeSearchPlanItem> EpisodeSearches);

    /// <summary>
    /// Groups the cycle's already-eligible (missing, monitored, due) episodes
    /// by series and season, and decides for each group whether it qualifies
    /// for a season search.
    /// </summary>
    /// <param name="dueEpisodes">
    /// The episodes the cycle already found missing, monitored and due for
    /// search — exactly what would otherwise go straight to
    /// <c>PlanEpisodeSearchesAsync</c>.
    /// </param>
    /// <param name="seasonInventory">
    /// For a series+season key that appears in <paramref name="dueEpisodes"/>,
    /// every monitored episode of that season as currently catalogued. Only
    /// looked up for groups with at least <see cref="MinimumSeasonSize"/> due
    /// episodes, since nothing smaller can ever qualify.
    /// </param>
    public static Plan Decide(
        IReadOnlyList<EpisodeSearchPlanItem> dueEpisodes,
        DateTimeOffset now,
        Func<string, int, IReadOnlyList<SeriesEpisodeInventoryItem>> seasonInventory)
    {
        if (dueEpisodes.Count == 0)
        {
            return new Plan([], []);
        }

        var seasonSearches = new List<SeasonSearchPlanItem>();
        var episodeSearches = new List<EpisodeSearchPlanItem>();

        foreach (var group in dueEpisodes.GroupBy(episode => (episode.SeriesId, episode.SeasonNumber)))
        {
            var groupEpisodes = group.ToArray();
            if (groupEpisodes.Length < MinimumSeasonSize)
            {
                episodeSearches.AddRange(groupEpisodes);
                continue;
            }

            var inSeason = seasonInventory(group.Key.SeriesId, group.Key.SeasonNumber);

            // A season with an episode that was removed or unmonitored never gets a pack: the pack would download that
            // episode again. Each due episode is searched on its own instead (#1072).
            if (inSeason.Any(episode => !episode.Monitored))
            {
                episodeSearches.AddRange(groupEpisodes);
                continue;
            }

            var monitored = inSeason
                .Where(episode => episode.Monitored)
                .ToArray();

            // Every monitored episode must have aired, in the past, with no
            // ambiguity. One unknown or future air date holds the whole
            // season to per-episode search.
            var fullyAired = monitored.Length > 0
                && monitored.All(episode => episode.AirDateUtc is { } aired && aired <= now);

            var missing = fullyAired
                ? monitored.Where(episode => !episode.HasFile).ToArray()
                : [];

            // Every monitored episode of the season must be missing — a
            // season with even one covered episode is a partial season, and
            // stays on the per-episode path.
            var seasonFullyMissing = fullyAired
                && missing.Length == monitored.Length
                && missing.Length >= MinimumSeasonSize;

            // The missing set must match the due set exactly: an episode
            // whose own retry window has not opened yet must not be swept
            // into a pack search, and a pack search must not leave a missing,
            // due episode out of both plans.
            var dueIds = groupEpisodes.Select(episode => episode.EpisodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingIds = missing.Select(episode => episode.EpisodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (seasonFullyMissing && dueIds.SetEquals(missingIds))
            {
                seasonSearches.Add(new SeasonSearchPlanItem(
                    group.Key.SeriesId,
                    // The episode plan items do not carry the series title;
                    // callers that need it (Activity, the job payload) look
                    // it up once per series rather than every episode
                    // carrying a copy of a value that only ever changes with
                    // the series itself.
                    SeriesTitle: string.Empty,
                    group.Key.SeasonNumber,
                    groupEpisodes
                        .OrderBy(episode => episode.EpisodeNumber)
                        .Select(episode => new SeasonSearchPlanEpisode(episode.EpisodeId, episode.EpisodeNumber, episode.Title))
                        .ToArray()));
            }
            else
            {
                episodeSearches.AddRange(groupEpisodes);
            }
        }

        return new Plan(seasonSearches, episodeSearches);
    }
}
