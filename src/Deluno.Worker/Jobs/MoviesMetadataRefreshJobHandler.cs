using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;

namespace Deluno.Worker.Jobs;

public sealed class MoviesMetadataRefreshJobHandler(
    IMetadataProvider metadataProvider,
    IMovieCatalogRepository movieCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
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

        // Stamped before the provider call and regardless of the outcome. The
        // backfill selects on staleness, and staleness was previously only
        // cleared by a successful match — so a title the provider cannot match
        // stayed stale forever and was re-queued on every pass.
        await movieCatalogRepository.RecordMetadataAttemptAsync(movie.Id, cancellationToken);

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

        if (match is null)
        {
            return $"No metadata match found for {movie.Title}.";
        }

        await movieCatalogRepository.UpdateMetadataAsync(movie.Id, match, cancellationToken);

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
