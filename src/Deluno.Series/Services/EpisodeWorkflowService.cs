using Deluno.Platform.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Series.Services;

public sealed class EpisodeWorkflowService(
    ISeriesCatalogRepository repository,
    IVersionedMediaPolicyEngine policyEngine) : IEpisodeWorkflowService
{
    public async Task<EpisodeWorkflowDecision> EvaluateEpisodeAsync(
        string episodeId,
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
        if (inventory is null)
        {
            return new EpisodeWorkflowDecision(
                EpisodeId: episodeId,
                Decision: "unknown",
                Reason: "Series not found");
        }

        var episode = inventory.Episodes.FirstOrDefault(e => e.EpisodeId == episodeId);
        if (episode is null)
        {
            return new EpisodeWorkflowDecision(
                EpisodeId: episodeId,
                Decision: "unknown",
                Reason: "Episode not found");
        }

        // If episode has file AND quality cutoff is met, it's archived
        if (episode.HasFile && episode.QualityCutoffMet)
        {
            return new EpisodeWorkflowDecision(
                EpisodeId: episodeId,
                Decision: "archived",
                Reason: "Has file and quality cutoff met");
        }

        // If episode is monitored but has no file, it's wanted
        if (episode.Monitored && !episode.HasFile)
        {
            return new EpisodeWorkflowDecision(
                EpisodeId: episodeId,
                Decision: "wanted",
                Reason: "Monitored but missing");
        }

        // If episode has file but quality cutoff not met, it's still wanted for upgrade
        if (episode.HasFile && !episode.QualityCutoffMet && episode.Monitored)
        {
            return new EpisodeWorkflowDecision(
                EpisodeId: episodeId,
                Decision: "wanted",
                Reason: "Has file but quality below cutoff");
        }

        return new EpisodeWorkflowDecision(
            EpisodeId: episodeId,
            Decision: "satisfied",
            Reason: "No action needed");
    }

    public async Task<int?> CalculateEpisodeQualityDeltaAsync(
        string episodeId,
        string libraryId,
        string candidateQuality,
        CancellationToken cancellationToken)
    {
        var currentQuality = await repository.GetEpisodeCurrentQualityAsync(episodeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(currentQuality) || string.IsNullOrWhiteSpace(candidateQuality))
        {
            return null;
        }

        var currentRank = policyEngine.QualityRank(currentQuality);
        var candidateRank = policyEngine.QualityRank(candidateQuality);

        if (currentRank < 0 || candidateRank < 0)
        {
            return null;
        }

        return candidateRank - currentRank;
    }
}
