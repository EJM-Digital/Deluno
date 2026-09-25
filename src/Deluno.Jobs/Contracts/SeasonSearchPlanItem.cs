namespace Deluno.Jobs.Contracts;

/// <summary>One episode of a season the automatic cycle plans to search as a pack (#646).</summary>
public sealed record SeasonSearchPlanEpisode(
    string EpisodeId,
    int EpisodeNumber,
    string Title);

/// <summary>
/// A season the automatic library cycle plans to search as one whole-season
/// search rather than one search per episode.
///
/// <para>Reserved for a season that is entirely missing and fully aired: every
/// monitored episode has aired and none of them has a file. The episodes it
/// carries are exactly the ones the pack search covers, so a fallback to
/// per-episode search — when no acceptable pack is found — has everything it
/// needs without asking the catalogue again.</para>
/// </summary>
public sealed record SeasonSearchPlanItem(
    string SeriesId,
    string SeriesTitle,
    int SeasonNumber,
    IReadOnlyList<SeasonSearchPlanEpisode> Episodes);
