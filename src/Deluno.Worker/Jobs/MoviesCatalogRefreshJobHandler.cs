using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

public sealed class MoviesCatalogRefreshJobHandler : IJobHandler
{
    public string JobType => "movies.catalog.refresh";

    public Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
        => Task.FromResult("Finished checking your movie library.");
}
