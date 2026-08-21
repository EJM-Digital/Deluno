namespace Deluno.Recovery.Contracts;

public interface IDownloadRetryService
{
    Task<DownloadRetryResult> RunRetryPassAsync(CancellationToken cancellationToken);
}

public sealed record DownloadRetryResult(
    int RetriedCount,
    int SkippedCount,
    string Summary);
