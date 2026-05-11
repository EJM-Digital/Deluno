using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

public interface IEpisodeWorkflowService
{
    Task<EpisodeWorkflowDecision> EvaluateEpisodeAsync(
        string episodeId,
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> CalculateEpisodeQualityDeltaAsync(
        string episodeId,
        string libraryId,
        string candidateQuality,
        CancellationToken cancellationToken);
}
