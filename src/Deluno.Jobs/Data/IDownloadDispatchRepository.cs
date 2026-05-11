namespace Deluno.Jobs.Data;

public interface IDownloadDispatchRepository
{
    Task<int> IncrementAttemptCountAsync(string dispatchId, CancellationToken cancellationToken);
    Task MarkDispatchFailedAsync(string dispatchId, CancellationToken cancellationToken);
}
