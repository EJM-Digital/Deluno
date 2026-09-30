using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;

namespace Deluno.Worker.Jobs;

public sealed class MoviesMetadataRefreshJobHandler(
    IMetadataProvider metadataProvider,
    IMovieCatalogRepository movieCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IAutoTaggingService? autoTagging = null) : IJobHandler
{
    public string JobType => "movies.metadata.refresh";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.RelatedEntityId))
        {
            return "Movie metadata refresh skipped because no movie was linked.";
        }

        var movie = await movieCatalogRepository.GetByIdAsync(job.RelatedEntityId, cancellationToken);
        if (movie is null)
        {
            return "Movie metadata refresh skipped because the movie no longer exists.";
        }

        // Stamped regardless of the outcome, so a title the provider cannot match is
        // not re-queued on every pass - EXCEPT when the provider could not answer
        // at all. A title whose refresh ends Unavailable, or comes back empty only
        // because the service failed, is not stamped: that would make it wait 24
        // hours for an outage of a minute and report it as "No metadata match"
        // (#1090).
        var providerUnavailable = false;
        try
        {
            return await RefreshAsync(job, movie, () => providerUnavailable = true, cancellationToken);
        }
        finally
        {
            if (!providerUnavailable)
            {
                await movieCatalogRepository.RecordMetadataAttemptAsync(movie.Id, CancellationToken.None);
            }
        }
    }

    private async Task<string> RefreshAsync(
        JobQueueItem job,
        MovieListItem movie,
        Action markProviderUnavailable,
        CancellationToken cancellationToken)
    {
        using var failureScope = metadataProvider.BeginFailureScope();

        MetadataSearchResult? match;
        if (!string.IsNullOrWhiteSpace(movie.MetadataProviderId))
        {
            var lookup = await metadataProvider.ResolveProviderRecordAsync(
                new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, movie.MetadataProviderId),
                cancellationToken);
            if (lookup.Status == MetadataProviderRecordStatus.Missing)
            {
                await RecordMissingProviderIssueAsync(movie.Id, movie.Title, lookup, job.Id, cancellationToken);
                return $"Kept {movie.Title}; its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.";
            }

            if (lookup.Status == MetadataProviderRecordStatus.Unavailable)
            {
                // A setup problem (no key, a bad stored id) is stamped like any other
                // outcome; only a service that did not answer is worth trying again soon.
                if (lookup.Failure?.IsTransient != false)
                {
                    markProviderUnavailable();
                }

                return $"Could not verify metadata for {movie.Title} because the provider is temporarily unavailable.";
            }

            match = lookup.Result;
        }
        else
        {
            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, null),
                cancellationToken);
            match = matches.FirstOrDefault();
        }

        if (match is null && failureScope?.Failure is not null)
        {
            markProviderUnavailable();
            return $"Could not look up {movie.Title} because the metadata service did not answer. Deluno will try again.";
        }

        if (match is not null && MetadataLinkPolicy.WouldDowngradeLink(movie.MetadataProvider, match))
        {
            return $"Kept {movie.Title} linked to {movie.MetadataProvider?.ToUpperInvariant()}; the refresh only found it on OMDb, and changing the source is left to Fix match.";
        }

        if (match is null)
        {
            return $"No metadata match found for {movie.Title}.";
        }

        await movieCatalogRepository.UpdateMetadataAsync(movie.Id, match, cancellationToken);

        // #1024: what the refresh learned (genres, language, certification) can change
        // which auto-tagging rules the title matches.
        if (autoTagging is not null)
        {
            await autoTagging.ApplyAsync(MediaKind.Movie, movie.Id, AutoTagTrigger.Refreshed, null, null, cancellationToken);
        }

        await activityFeedRepository.RecordActivityAsync(
            "metadata.movie.refreshed",
            $"{movie.Title} metadata was refreshed by the background worker.",
            JsonSerializer.Serialize(match, JobPayloads.Options),
            job.Id,
            "movie",
            movie.Id,
            cancellationToken);

        return $"Refreshed metadata for {movie.Title}.";
    }

    private async Task RecordMissingProviderIssueAsync(
        string movieId,
        string title,
        MetadataProviderRecordLookup lookup,
        string jobId,
        CancellationToken cancellationToken)
    {
        var evidenceKey = $"{lookup.Provider}:movie:{lookup.ProviderId}:missing".ToLowerInvariant();
        var isNewEvidence = await movieCatalogRepository.RecordMetadataProviderIssueAsync(
            movieId,
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
            "metadata.movie.provider-record-missing",
            $"{title} was kept in Deluno because its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.",
            JsonSerializer.Serialize(new { lookup.Provider, lookup.ProviderId, EvidenceKey = evidenceKey }, JobPayloads.Options),
            jobId,
            "movie",
            movieId,
            cancellationToken);
    }
}
