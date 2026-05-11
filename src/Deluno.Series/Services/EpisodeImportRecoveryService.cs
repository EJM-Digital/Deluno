using Deluno.Series.Data;

namespace Deluno.Series.Services;

public sealed class EpisodeImportRecoveryService(ISeriesCatalogRepository seriesCatalogRepository)
    : IEpisodeImportRecoveryService
{
    public async Task<IReadOnlyList<string>> FindEpisodesNeedingRecoveryAsync(
        string libraryId,
        CancellationToken cancellationToken)
    {
        // Phase 5 Implementation:
        // Queries episodes with file but quality below target using inventory detail
        // Filters to those that are monitored and have quality_cutoff_met = 0
        // Returns episode IDs needing re-download in priority order

        var series = await seriesCatalogRepository.ListAsync(cancellationToken);
        var episodesNeedingRecovery = new List<string>();

        foreach (var singleSeries in series)
        {
            var inventory = await seriesCatalogRepository.GetInventoryDetailAsync(singleSeries.Id, cancellationToken);
            if (inventory is null)
            {
                continue;
            }

            var needsRecovery = inventory.Episodes
                .Where(e => e.HasFile && !e.QualityCutoffMet && e.Monitored)
                .OrderBy(e => e.UpdatedUtc)
                .Take(5);

            foreach (var episode in needsRecovery)
            {
                episodesNeedingRecovery.Add(episode.EpisodeId);
            }
        }

        return episodesNeedingRecovery.Take(20).ToList();
    }

    public async Task<int> RecoveryPriorityAsync(string episodeId, CancellationToken cancellationToken)
    {
        // Phase 5 Implementation:
        // Calculates recovery priority based on last update time
        // Older updates get higher priority scores

        var series = await seriesCatalogRepository.ListAsync(cancellationToken);

        foreach (var singleSeries in series)
        {
            var inventory = await seriesCatalogRepository.GetInventoryDetailAsync(singleSeries.Id, cancellationToken);
            if (inventory is null)
            {
                continue;
            }

            var episode = inventory.Episodes.FirstOrDefault(e => e.EpisodeId == episodeId);
            if (episode is not null)
            {
                var ageHours = (int)((DateTimeOffset.UtcNow - episode.UpdatedUtc).TotalHours);
                return Math.Max(100, ageHours);
            }
        }

        return 0;
    }
}
