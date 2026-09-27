using Deluno.Integrations.Metadata.SceneMapping;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Services;

public sealed class SceneMappingRefreshService(
    ISeriesCatalogRepository seriesCatalogRepository,
    ISceneMappingProvider sceneMappingProvider,
    ILogger<SceneMappingRefreshService> logger)
    : ISceneMappingRefreshService
{
    public async Task<SceneMappingRefreshSummary> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var series = await seriesCatalogRepository.ListAsync(cancellationToken);
        var animeSeries = series.Where(item => item.SeriesType == SeriesTypes.Anime).ToArray();

        var withoutTvDbId = 0;
        var withCorrections = 0;
        var episodesUpdated = 0;

        foreach (var item in animeSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tvdbId = SeriesMetadataJson.ReadTvDbId(item.MetadataJson);
            if (string.IsNullOrWhiteSpace(tvdbId))
            {
                withoutTvDbId++;
                continue;
            }

            IReadOnlyList<SceneMappingEntry> entries;
            try
            {
                entries = await sceneMappingProvider.GetMappingAsync(tvdbId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One title's numbering source failing must not stop the rest
                // of the anime library from being checked.
                logger.LogWarning(
                    exception,
                    "Scene mapping lookup failed for {Title} (TVDB {TvdbId}).",
                    item.Title,
                    tvdbId);
                continue;
            }

            if (entries.Count == 0)
            {
                continue;
            }

            var mappings = entries
                .Select(entry => new SeriesEpisodeSceneMapping(
                    entry.ProviderSeasonNumber,
                    entry.ProviderEpisodeNumber,
                    entry.AbsoluteNumber,
                    entry.SceneSeasonNumber,
                    entry.SceneEpisodeNumber))
                .ToArray();

            var updated = await seriesCatalogRepository.ApplyAutomaticNumberingAsync(item.Id, mappings, cancellationToken);
            if (updated > 0)
            {
                withCorrections++;
                episodesUpdated += updated;
            }
        }

        return new SceneMappingRefreshSummary(animeSeries.Length, withoutTvDbId, withCorrections, episodesUpdated);
    }
}
