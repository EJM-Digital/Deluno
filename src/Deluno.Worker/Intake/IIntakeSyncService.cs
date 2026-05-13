namespace Deluno.Worker.Intake;

public interface IIntakeSyncService
{
    Task<int> PlanDueSyncJobsAsync(CancellationToken cancellationToken);

    Task<IntakeSyncRunResult> RunAsync(string sourceId, string? relatedJobId, bool manual, CancellationToken cancellationToken);
}

public sealed record IntakeSyncRunResult(
    string SourceId,
    string SourceName,
    string Status,
    int FetchedCount,
    int AddedCount,
    int DuplicateCount,
    int SkippedCount,
    int ErrorCount,
    bool SearchRequested,
    string Summary);
