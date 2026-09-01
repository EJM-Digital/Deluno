using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Movies.Services;

namespace Deluno.Worker.Jobs;

public sealed class MovieCollectionSyncJobHandler(
    IMovieCollectionService movieCollectionService) : IJobHandler
{
    public string JobType => MovieCollectionJobTypes.Sync;

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.RelatedEntityId))
        {
            return "Movie collection refresh skipped because no collection was linked.";
        }

        var result = await movieCollectionService.SyncAsync(job.RelatedEntityId, cancellationToken);
        return result.Message;
    }
}
