namespace Deluno.Jobs.Contracts;

public interface IDispatchMetricsRepository
{
    Task<DispatchMetrics> GetMetricsAsync(CancellationToken cancellationToken);

    Task RecordDispatchOutcomeAsync(
        string dispatchId,
        string mediaType,
        string? grabStatus,
        string? importStatus,
        DateTimeOffset? grabAttemptedUtc,
        DateTimeOffset? detectedUtc,
        DateTimeOffset? importCompletedUtc,
        CancellationToken cancellationToken);
}
