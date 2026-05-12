using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

public interface IEpisodeWorkflowService
{
    Task<EpisodeWorkflowDecision> EvaluateEpisodeAsync(
        string episodeId,
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the quality delta (candidateRank - currentRank) for the episode.
    /// Positive = upgrade, zero = same, negative = downgrade, null = quality unknown.
    /// </summary>
    Task<int?> CalculateEpisodeQualityDeltaAsync(
        string episodeId,
        string libraryId,
        string candidateQuality,
        CancellationToken cancellationToken);
}
