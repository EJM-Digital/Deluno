namespace Deluno.Series.Services;

/// <summary>What one refresh pass actually did, for the scheduled task's log line.</summary>
public sealed record EpisodeGroupRefreshSummary(
    int SeriesChecked,
    int SeriesWithoutProviderId,
    int SeriesWithUpdatedOrderings,
    int EpisodesUpdated);

/// <summary>
/// Keeps every TMDb-backed title's available episode orderings (DVD order,
/// absolute numbering, and whatever else TMDb's episode_groups endpoint
/// publishes) current, without a person ever opening the numbering editor
/// (#863). Split out from the hosted service that schedules it, the same way
/// <see cref="ISceneMappingRefreshService"/> is - so the refresh itself can be
/// exercised directly in a test.
/// </summary>
public interface IEpisodeGroupRefreshService
{
    Task<EpisodeGroupRefreshSummary> RefreshAllAsync(CancellationToken cancellationToken);
}
