using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public interface IJobScheduler
{
    Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken);
}
