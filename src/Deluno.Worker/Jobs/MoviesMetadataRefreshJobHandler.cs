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

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, movie.MetadataProviderId),
            cancellationToken);
        var match = matches.FirstOrDefault();
        if (match is null)
        {
            return $"No metadata match found for {movie.Title}.";
        }

        await movieCatalogRepository.UpdateMetadataAsync(
            movie.Id,
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
}
