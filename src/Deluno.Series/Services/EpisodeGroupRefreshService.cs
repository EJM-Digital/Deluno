using Deluno.Integrations.Metadata;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Services;

public sealed class EpisodeGroupRefreshService(
    ISeriesCatalogRepository seriesCatalogRepository,
    IMetadataProvider metadataProvider,
    ILogger<EpisodeGroupRefreshService> logger)
    : IEpisodeGroupRefreshService
{
    public async Task<EpisodeGroupRefreshSummary> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var series = await seriesCatalogRepository.ListAsync(cancellationToken);
        // TMDb episode groups are not an anime-only feature (DVD order and
        // story arcs exist for plenty of standard shows too), so unlike the
        // scene-mapping pass this checks every TMDb-backed title.
        var tmdbSeries = series
            .Where(item => string.Equals(item.MetadataProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.MetadataProviderId))
            .ToArray();

        var withoutProviderId = series.Count - tmdbSeries.Length;
        var withUpdates = 0;
        var episodesUpdated = 0;

        foreach (var item in tmdbSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MetadataEpisodeGroup> groups;
            try
            {
                // An ordering is keyed by the aired numbering of the provider it
                // came from. TheTVDB's are used only for a show whose episodes
                // sit on TheTVDB's numbering (or line up with it); a show still
                // on TMDb's numbering keeps TMDb's orderings, so no episode is
                // handed another episode's absolute or DVD number.
                var inventory = await seriesCatalogRepository.GetInventoryDetailAsync(item.Id, cancellationToken);
                var choice = await MetadataCatalogueChoice.ChooseAsync(
                    metadataProvider,
                    item.MetadataProviderId!,
                    SeriesCatalogueChoice.ExistingEpisodes(inventory),
                    cancellationToken);
                if (choice.Hold != MetadataCatalogueHold.None)
                {
                    // The show sits on TheTVDB's numbering and TheTVDB cannot be
                    // reached or does not agree: leave its orderings as they are.
                    continue;
                }

                var catalogue = choice.Seasons;
                groups = await metadataProvider.GetSeriesEpisodeGroupsAsync(
                    item.MetadataProviderId!,
                    MetadataCatalogueChoice.SourceName(catalogue) == "tvdb"
                        ? MetadataCatalogueSource.Preferred
                        : MetadataCatalogueSource.TmdbOnly,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One title's provider lookup failing must not stop the rest
                // of the library from being checked.
                logger.LogWarning(
                    exception,
                    "Episode-group lookup failed for {Title} (TMDb {ProviderId}).",
                    item.Title,
                    item.MetadataProviderId);
                continue;
            }

            if (groups.Count == 0)
            {
                continue;
            }

            var availableSchemes = new List<string>();
            var otherSchemeNames = new List<string>();
            var byOriginalEpisode = new Dictionary<(int Season, int Episode), (int? Absolute, int? DvdSeason, int? DvdEpisode)>();

            foreach (var group in groups)
            {
                var scheme = group.TmdbType switch
                {
                    TmdbEpisodeGroupTypes.Absolute => SeriesNumberingSchemes.Absolute,
                    TmdbEpisodeGroupTypes.Dvd => SeriesNumberingSchemes.Dvd,
                    _ => null
                };

                if (scheme is null)
                {
                    otherSchemeNames.Add(group.Name);
                    continue;
                }

                if (group.Episodes.Count == 0)
                {
                    continue;
                }

                availableSchemes.Add(scheme);
                foreach (var episode in group.Episodes)
                {
                    var key = (episode.OriginalSeasonNumber, episode.OriginalEpisodeNumber);
                    var existing = byOriginalEpisode.TryGetValue(key, out var current)
                        ? current
                        : (null, null, null);

                    byOriginalEpisode[key] = scheme == SeriesNumberingSchemes.Absolute
                        ? (episode.GroupEpisodeNumber, existing.DvdSeason, existing.DvdEpisode)
                        : (existing.Absolute, episode.GroupSeasonNumber, episode.GroupEpisodeNumber);
                }
            }

            var mappings = byOriginalEpisode
                .Select(entry => new SeriesEpisodeGroupMapping(
                    entry.Key.Season,
                    entry.Key.Episode,
                    entry.Value.Absolute,
                    entry.Value.DvdSeason,
                    entry.Value.DvdEpisode))
                .ToArray();

            var updated = await seriesCatalogRepository.ApplyEpisodeGroupsAsync(
                item.Id,
                availableSchemes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                otherSchemeNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                mappings,
                cancellationToken);

            if (updated > 0 || availableSchemes.Count > 0 || otherSchemeNames.Count > 0)
            {
                withUpdates++;
                episodesUpdated += updated;
            }
        }

        return new EpisodeGroupRefreshSummary(tmdbSeries.Length, withoutProviderId, withUpdates, episodesUpdated);
    }
}
