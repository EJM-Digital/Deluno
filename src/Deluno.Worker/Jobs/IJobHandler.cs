using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Handles one job type. Each implementation takes only the dependencies its
/// own work needs through its constructor, instead of receiving every
/// dependency every job type could ever need.
/// </summary>
public interface IJobHandler
{
    string JobType { get; }

    Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken);
}
