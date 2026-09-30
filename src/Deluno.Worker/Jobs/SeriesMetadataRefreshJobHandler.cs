using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;

namespace Deluno.Worker.Jobs;

public sealed class SeriesMetadataRefreshJobHandler(
    IMetadataProvider metadataProvider,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IAutoTaggingService? autoTagging = null,
    IHealthCheckRepository? healthCheckRepository = null) : IJobHandler
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

        // Stamped regardless of the outcome (see the movie handler), EXCEPT when the
        // provider could not answer: a title whose refresh ends Unavailable must not
        // wait 24 hours for an outage of a minute (#1090).
        var providerUnavailable = false;
        try
        {
            return await RefreshAsync(job, series, () => providerUnavailable = true, cancellationToken);
        }
        finally
        {
            if (!providerUnavailable)
            {
                await seriesCatalogRepository.RecordMetadataAttemptAsync(series.Id, CancellationToken.None);
            }
        }
    }

    private async Task<string> RefreshAsync(
        JobQueueItem job,
        SeriesListItem series,
        Action markProviderUnavailable,
        CancellationToken cancellationToken)
    {
        using var failureScope = metadataProvider.BeginFailureScope();

        MetadataSearchResult? match;
        if (!string.IsNullOrWhiteSpace(series.MetadataProviderId))
        {
            var lookup = await metadataProvider.ResolveProviderRecordAsync(
                new MetadataLookupRequest(series.Title, "tv", series.StartYear, series.MetadataProviderId),
                cancellationToken);
            if (lookup.Status == MetadataProviderRecordStatus.Missing)
            {
                await RecordMissingProviderIssueAsync(series.Id, series.Title, lookup, job.Id, cancellationToken);
                return $"Kept {series.Title}; its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.";
            }

            if (lookup.Status == MetadataProviderRecordStatus.Unavailable)
            {
                if (lookup.Failure?.IsTransient != false)
                {
                    markProviderUnavailable();
                }

                return $"Could not verify metadata for {series.Title} because the provider is temporarily unavailable.";
            }

            match = lookup.Result;
        }
        else
        {
            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(series.Title, "tv", series.StartYear, null),
                cancellationToken);
            match = matches.FirstOrDefault();
        }

        if (match is null && failureScope?.Failure is not null)
        {
            markProviderUnavailable();
            return $"Could not look up {series.Title} because the metadata service did not answer. Deluno will try again.";
        }

        if (match is not null && MetadataLinkPolicy.WouldDowngradeLink(series.MetadataProvider, match))
        {
            return $"Kept {series.Title} linked to {series.MetadataProvider?.ToUpperInvariant()}; the refresh only found it on OMDb, and changing the source is left to Fix match.";
        }

        if (match is null)
        {
            return $"No metadata match found for {series.Title}.";
        }

        await seriesCatalogRepository.UpdateMetadataAsync(series.Id, match, cancellationToken);

        // #1024: what the refresh learned (genres, status, language) can change
        // which auto-tagging rules the show matches.
        if (autoTagging is not null)
        {
            await autoTagging.ApplyAsync(MediaKind.Series, series.Id, AutoTagTrigger.Refreshed, null, null, cancellationToken);
        }

        // Re-syncing the catalogue on the schedule is how an episode announced
        // after the show was added ever becomes known. Without it the inventory
        // is only as current as the day someone last pressed Refresh.
        var catalogue = await SyncSeriesCatalogueAsync(series.Id, series.Title, match.ProviderId, job.Id, cancellationToken);

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

    private async Task RecordMissingProviderIssueAsync(
        string seriesId,
        string title,
        MetadataProviderRecordLookup lookup,
        string jobId,
        CancellationToken cancellationToken)
    {
        var evidenceKey = $"{lookup.Provider}:series:{lookup.ProviderId}:missing".ToLowerInvariant();
        var isNewEvidence = await seriesCatalogRepository.RecordMetadataProviderIssueAsync(
            seriesId,
            new MetadataProviderIssue(
                "provider-record-missing",
                lookup.Provider,
                lookup.ProviderId,
                evidenceKey,
                DateTimeOffset.UtcNow,
                null),
            cancellationToken);

        if (!isNewEvidence)
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.provider-record-missing",
            $"{title} was kept in Deluno because its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.",
            JsonSerializer.Serialize(new { lookup.Provider, lookup.ProviderId, EvidenceKey = evidenceKey }, JobPayloads.Options),
            jobId,
            "series",
            seriesId,
            cancellationToken);
    }

    /// <summary>
    /// Pull the provider's season/episode list into the inventory. A provider
    /// that cannot answer leaves the catalogue as it was — never a failed job.
    /// </summary>
    private async Task<SeriesCatalogueSyncResult> SyncSeriesCatalogueAsync(
        string seriesId,
        string seriesTitle,
        string? providerId,
        string? jobId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return SeriesCatalogueSyncResult.None;
        }

        using var failureScope = metadataProvider.BeginFailureScope();
        var hadEpisodes = false;
        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            // TheTVDB's list when it lines up with the episodes this show
            // already has, TMDb's when it would strand any of them.
            var inventory = await seriesCatalogRepository.GetInventoryDetailAsync(seriesId, cancellationToken);
            hadEpisodes = (inventory?.Episodes?.Count ?? 0) > 0;
            var choice = await MetadataCatalogueChoice.ChooseAsync(
                metadataProvider,
                providerId,
                SeriesCatalogueChoice.ExistingEpisodes(inventory),
                cancellationToken);
            if (choice.Hold != MetadataCatalogueHold.None)
            {
                // A show numbered by TheTVDB is never re-synced from TMDb's
                // list: its episodes stay and the next refresh tries again.
                await SeriesCatalogueChoice.RecordHoldAsync(
                    activityFeedRepository, healthCheckRepository, choice.Hold, seriesId, seriesTitle, jobId, cancellationToken);
                return SeriesCatalogueSyncResult.None;
            }

            await SeriesCatalogueChoice.ClearHoldAsync(healthCheckRepository, seriesId, cancellationToken);

            seasons = choice.Seasons;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Used to vanish here with no word to anyone (#1090). The episodes Deluno
            // has are left as they were; the show says so, and the note is clearable.
            await NoteCatalogueProblemAsync(seriesId, seriesTitle, providerId, CatalogueUnreadable, jobId, cancellationToken);
            return SeriesCatalogueSyncResult.None;
        }

        var episodes = seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => new CatalogueEpisodeItem(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.Title,
                episode.Overview,
                episode.AirDateUtc,
                episode.AbsoluteNumber))
            .ToArray();

        var failure = failureScope?.Failure;
        if (episodes.Length == 0 && failure is not null)
        {
            // Empty because the service did not answer, not because the show has
            // no episodes (#1090).
            await NoteCatalogueProblemAsync(seriesId, seriesTitle, providerId, CatalogueUnreadable, jobId, cancellationToken);
            return SeriesCatalogueSyncResult.None;
        }

        if (failure is not null && !hadEpisodes && MetadataCatalogueChoice.SourceName(seasons) == "tmdb")
        {
            // A new show synced while TheTVDB was not answering locks in TMDb's
            // numbering; say so, so it is not a surprise later (#1090).
            await NoteCatalogueProblemAsync(seriesId, seriesTitle, providerId, CatalogueTvdbSkipped, jobId, cancellationToken);
        }
        else if (failure is null)
        {
            await ClearCatalogueNoteAsync(seriesId, cancellationToken);
        }

        return episodes.Length == 0
            ? SeriesCatalogueSyncResult.None
            : await seriesCatalogRepository.SyncEpisodeCatalogueAsync(
                seriesId,
                episodes,
                MetadataCatalogueChoice.SourceName(seasons),
                cancellationToken);
    }

    /// <summary>The episode list could not be read at all; the show's episodes are left as they were.</summary>
    public const string CatalogueUnreadable = "catalogue-unreadable";

    /// <summary>TheTVDB did not answer for a new show, so its episodes use TMDb's numbering.</summary>
    public const string CatalogueTvdbSkipped = "catalogue-tvdb-unavailable";

    /// <summary>
    /// A title-scoped note on the show's own page, in the same place (and with
    /// the same Clear) as the "no longer listed" note. One per show; a new day's
    /// outage raises it again, the same day's does not.
    /// </summary>
    private async Task NoteCatalogueProblemAsync(
        string seriesId,
        string seriesTitle,
        string? providerId,
        string kind,
        string? jobId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var evidenceKey = $"tmdb:series:{providerId}:{kind}:{now:yyyy-MM-dd}";
        var isNew = await seriesCatalogRepository.RecordMetadataProviderIssueAsync(
            seriesId,
            new MetadataProviderIssue(kind, "tmdb", providerId ?? string.Empty, evidenceKey, now, null),
            cancellationToken);
        if (!isNew)
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.catalogue.unreadable",
            kind == CatalogueTvdbSkipped
                ? $"TheTVDB did not answer, so {seriesTitle}'s episodes use TMDb's numbering for now."
                : $"Deluno could not read {seriesTitle}'s episode list because the metadata service did not answer. The episodes it has were left as they are.",
            null,
            jobId,
            "series",
            seriesId,
            cancellationToken);
    }

    /// <summary>A refresh that read the list cleanly ends its own note; a different note (a missing record) is left alone.</summary>
    private async Task ClearCatalogueNoteAsync(string seriesId, CancellationToken cancellationToken)
    {
        var issue = await seriesCatalogRepository.GetMetadataProviderIssueAsync(seriesId, cancellationToken);
        if (issue is not null && issue.Kind is CatalogueUnreadable or CatalogueTvdbSkipped)
        {
            await seriesCatalogRepository.ClearMetadataProviderIssueAsync(seriesId, cancellationToken);
        }
    }
}
