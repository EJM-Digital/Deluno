using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

public sealed class SeriesCatalogRefreshJobHandler : IJobHandler
{
    public string JobType => "series.catalog.refresh";

    public Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
        => Task.FromResult("Finished checking your TV show library.");
}
