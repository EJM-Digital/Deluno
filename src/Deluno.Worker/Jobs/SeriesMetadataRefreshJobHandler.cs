using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

public sealed class SeriesMetadataRefreshJobHandler(
    IMetadataProvider metadataProvider,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
{
    public string JobType => "series.metadata.refresh";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.RelatedEntityId))
        {
            return "TV metadata refresh skipped because no series was linked.";
        }

        var series = await seriesCatalogRepository.GetByIdAsync(job.RelatedEntityId, cancellationToken);
        if (series is null)
        {
            return "TV metadata refresh skipped because the series no longer exists.";
        }

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(series.Title, "tv", series.StartYear, series.MetadataProviderId),
            cancellationToken);
        var match = matches.FirstOrDefault();
        if (match is null)
        {
            return $"No metadata match found for {series.Title}.";
        }

        await seriesCatalogRepository.UpdateMetadataAsync(
            series.Id,
            match.Provider,
            match.ProviderId,
            match.OriginalTitle,
            match.Overview,
            match.PosterUrl,
            match.BackdropUrl,
            match.Rating,
            string.Join(", ", match.Genres),
            match.ExternalUrl,
            match.ImdbId,
            JsonSerializer.Serialize(match, JobPayloads.Options),
            cancellationToken);

        // Re-syncing the catalogue on the schedule is how an episode announced
        // after the show was added ever becomes known. Without it the inventory
        // is only as current as the day someone last pressed Refresh.
        var catalogue = await SyncSeriesCatalogueAsync(series.Id, match.ProviderId, cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.refreshed",
            $"{series.Title} metadata was refreshed by the background worker.",
            JsonSerializer.Serialize(match, JobPayloads.Options),
            job.Id,
            "series",
            series.Id,
            cancellationToken);

        if (catalogue.AddedCount > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.catalogue",
                $"Deluno learned {catalogue.AddedCount} more episode{(catalogue.AddedCount == 1 ? "" : "s")} of {series.Title}.",
                JsonSerializer.Serialize(catalogue, JobPayloads.Options),
                job.Id,
                "series",
                series.Id,
                cancellationToken);

            return $"Refreshed metadata for {series.Title} and added {catalogue.AddedCount} newly announced episode{(catalogue.AddedCount == 1 ? "" : "s")}.";
        }

        return $"Refreshed metadata for {series.Title}.";
    }

    /// <summary>
    /// Pull the provider's season/episode list into the inventory. A provider
    /// that cannot answer leaves the catalogue as it was — never a failed job.
    /// </summary>
    private async Task<SeriesCatalogueSyncResult> SyncSeriesCatalogueAsync(
        string seriesId,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return SeriesCatalogueSyncResult.None;
        }

        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            seasons = await metadataProvider.GetSeriesCatalogueAsync(providerId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SeriesCatalogueSyncResult.None;
        }

        var episodes = seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => new CatalogueEpisodeItem(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.Title,
                episode.Overview,
                episode.AirDateUtc))
            .ToArray();

        return episodes.Length == 0
            ? SeriesCatalogueSyncResult.None
            : await seriesCatalogRepository.SyncEpisodeCatalogueAsync(seriesId, episodes, "tmdb", cancellationToken);
    }
}
