namespace Deluno.Series.Services;

/// <summary>What one refresh pass actually did, for the scheduled task's log line.</summary>
public sealed record SceneMappingRefreshSummary(
    int AnimeSeriesChecked,
    int SeriesWithoutTvDbId,
    int SeriesWithCorrections,
    int EpisodesUpdated);

/// <summary>
/// Keeps anime titles' scene/absolute numbering current against TheXEM (or
/// whatever <see cref="Deluno.Integrations.Metadata.SceneMapping.ISceneMappingProvider"/>
/// is registered) without a person ever opening the numbering editor. Split
/// out from the hosted service that schedules it so the refresh itself can be
/// exercised directly in a test, the same way <c>IReleaseRankingModelAdminService</c>
/// sits behind <c>RankingModelTrainingHostedService</c>.
/// </summary>
public interface ISceneMappingRefreshService
{
    Task<SceneMappingRefreshSummary> RefreshAllAsync(CancellationToken cancellationToken);
}
