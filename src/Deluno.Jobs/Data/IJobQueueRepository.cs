using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public interface IJobQueueRepository
{
    Task<IReadOnlyList<JobQueueItem>> ListAsync(int take, CancellationToken cancellationToken);

    Task<Page<JobQueueItem>> ListPageAsync(PageRequest request, CancellationToken cancellationToken);

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

    /// <summary>
    /// When the next job of these types becomes runnable, or <c>null</c> when
    /// there is none.
    ///
    /// <para><b>This is what lets a lane sleep instead of poll.</b> A lane that
    /// leases nothing has two honest options: come back on a fixed tick and ask
    /// again, or find out when there is actually something to come back for. The
    /// first is what every lane used to do — seven lanes, twice a minute, mostly
    /// to be told nothing had changed, which is AUDIT-001 finding 4 in
    /// miniature. The second costs one indexed lookup and then nothing at all
    /// until the moment it matters.</para>
    ///
    /// <para>Served by <c>ix_job_queue_type_status_scheduled</c>, which already
    /// leads on exactly these columns.</para>
    /// </summary>
    Task<DateTimeOffset?> NextDueUtcAsync(
        IReadOnlyList<string> jobTypes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leases up to <paramref name="maxJobs"/> ready jobs in one transaction, so
    /// throughput is bounded by the machine rather than by a caller's tick rate.
    /// </summary>
    Task<IReadOnlyList<JobQueueItem>> LeaseBatchAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? jobTypes,
        int maxJobs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases work held by a previous incarnation of this worker. Callers
    /// provide exact lane worker ids, rather than a broad host prefix, so a
    /// restart never touches another worker on a similarly named machine.
    /// Attempts are deliberately preserved: releasing makes work runnable, it
    /// does not pretend the interrupted attempt never happened.
    /// </summary>
    Task<IReadOnlyList<JobQueueItem>> ReleaseLeasesAsync(
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken);

    Task CompleteAsync(string jobId, string workerId, string? completionMessage, CancellationToken cancellationToken);

    Task FailAsync(string jobId, string workerId, string errorMessage, CancellationToken cancellationToken);

    Task HeartbeatAsync(string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// How many jobs of this type are still going to be worked — queued,
    /// running, or failed with retries left. Lets a planner top a queue up to
    /// a target depth instead of either queueing everything at once or
    /// queueing a fixed number per pass regardless of backlog.
    /// </summary>
    Task<int> CountActiveJobsAsync(string jobType, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically checks and claims a recurring background pass. Returns
    /// <c>true</c> only when the caller is the one that gets to run it — either
    /// no prior run is recorded, or the last one is older than
    /// <paramref name="interval"/>. Two hosts calling this concurrently for the
    /// same key never both receive <c>true</c>.
    /// </summary>
    Task<bool> TryClaimScheduledPassAsync(string scheduleKey, TimeSpan interval, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, LibraryAutomationStateItem>> ListLibraryAutomationStatesAsync(CancellationToken cancellationToken);

    Task<Page<LibraryAutomationStateItem>> ListLibraryAutomationStatesPageAsync(
        PageRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the runtime row for a newly created library immediately. The
    /// library configuration remains the source of truth; this row only holds
    /// live scheduler state such as last/next run and errors.
    /// </summary>
    Task EnsureLibraryAutomationStateAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken);

    Task RemoveLibraryAutomationStateAsync(
        string libraryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchCycleRunItem>> ListSearchCycleRunsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<Page<SearchCycleRunItem>> ListSearchCycleRunsPageAsync(
        PageRequest request,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchRetryWindowItem>> ListSearchRetryWindowsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<Page<SearchRetryWindowItem>> ListSearchRetryWindowsPageAsync(
        PageRequest request,
        string? libraryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadDispatchItem>> ListDownloadDispatchesAsync(
        int take,
        string? mediaType,
        CancellationToken cancellationToken);

    Task<Page<DownloadDispatchItem>> ListDownloadDispatchesPageAsync(
        PageRequest request,
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

    /// <summary>
    /// The catalogue item a recently dispatched release belongs to, so an
    /// import can be named from the title Deluno knows rather than from the
    /// release name the download client reports (#268).
    /// </summary>
    Task<DispatchCatalogueLink?> FindRecentDispatchLinkAsync(
        string downloadClientId,
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>Per-day job and dispatch counts for the dashboard.</summary>
    Task<JobDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
