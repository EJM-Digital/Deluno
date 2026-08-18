using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public interface IJobQueueRepository
{
    Task<IReadOnlyList<JobQueueItem>> ListAsync(int take, CancellationToken cancellationToken);

    Task<int> RetryFailedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes work that has not started for a title which is no longer managed.
    /// Running work is deliberately left alone so workers can finish safely.
    /// </summary>
    Task<int> CancelPendingForRelatedEntityAsync(
        string relatedEntityType,
        string relatedEntityId,
        CancellationToken cancellationToken);

    Task<JobQueueItem?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? jobTypes,
        CancellationToken cancellationToken);

    Task CompleteAsync(string jobId, string workerId, string? completionMessage, CancellationToken cancellationToken);

    Task FailAsync(string jobId, string workerId, string errorMessage, CancellationToken cancellationToken);

    Task HeartbeatAsync(string workerId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, LibraryAutomationStateItem>> ListLibraryAutomationStatesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchCycleRunItem>> ListSearchCycleRunsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchRetryWindowItem>> ListSearchRetryWindowsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadDispatchItem>> ListDownloadDispatchesAsync(
        int take,
        string? mediaType,
        CancellationToken cancellationToken);

    Task<bool> RequestLibrarySearchAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken);

    Task<bool> SkipLibrarySearchCycleAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken);

    Task PlanLibrarySearchesAsync(
        IReadOnlyList<LibraryAutomationPlanItem> libraries,
        CancellationToken cancellationToken);

    Task PlanEpisodeSearchesAsync(
        string libraryId,
        IReadOnlyList<EpisodeSearchPlanItem> episodes,
        CancellationToken cancellationToken);

    Task<string> RecordDownloadDispatchAsync(
        string libraryId,
        string mediaType,
        string entityType,
        string entityId,
        string releaseName,
        string indexerName,
        string downloadClientId,
        string downloadClientName,
        string status,
        string? notesJson,
        int? grabResponseCode = null,
        string? grabFailureCode = null,
        CancellationToken cancellationToken = default);

    Task RecordSearchCycleRunAsync(
        RecordSearchCycleRunRequest request,
        CancellationToken cancellationToken);

    Task RecordSearchRetryWindowAsync(
        string entityType,
        string entityId,
        string libraryId,
        string mediaType,
        string actionKind,
        DateTimeOffset nextEligibleUtc,
        DateTimeOffset lastAttemptUtc,
        string? lastResult,
        CancellationToken cancellationToken);

    Task<string?> FindRecentDispatchIdAsync(
        string downloadClientId,
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>Per-day job and dispatch counts for the dashboard.</summary>
    Task<JobDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
