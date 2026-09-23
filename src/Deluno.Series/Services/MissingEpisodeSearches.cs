using Deluno.Jobs.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

/// <summary>
/// The episodes a show's <b>Search now</b> asks for (#593).
///
/// <para>The show page offers "Find 67 missing episodes", and the button under
/// it ran one show-level query and asked for none of them. Each missing
/// episode now gets its own search, judged against its own file (#591), the
/// same job the library cycle plans for the episodes it reaches (#303).</para>
///
/// <para>Missing means what the page counts: aired and not held. An episode
/// still to air is left alone, and so is one a person stopped monitoring.</para>
/// </summary>
public static class MissingEpisodeSearches
{
    public static IReadOnlyList<EpisodeSearchPlanItem> For(
        string seriesId,
        IEnumerable<SeriesEpisodeInventoryItem> episodes,
        DateTimeOffset now)
        => episodes
            .Where(episode => episode.Monitored
                && !episode.HasFile
                && episode.AirDateUtc is { } aired
                && aired <= now)
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber)
            .Select(episode => new EpisodeSearchPlanItem(
                episode.EpisodeId,
                seriesId,
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.Title ?? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"))
            .ToArray();
}
