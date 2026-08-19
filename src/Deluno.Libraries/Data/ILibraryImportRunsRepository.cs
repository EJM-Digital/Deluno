using Deluno.Contracts;

namespace Deluno.Libraries.Data;

/// <summary>
/// Storage for existing-library import runs and the items they set aside.
///
/// Every read here is bounded — a single run row, or a capped page of issues.
/// Nothing in this interface returns "all runs" or "all issues", because at
/// 20,000 items per run those are exactly the queries that would grow with the
/// library.
/// </summary>
public interface ILibraryImportRunsRepository
{
    /// <summary>
    /// Creates a run, or returns the one already in flight for this library.
    /// A partial unique index makes "one active run per library" a database
    /// guarantee, so two simultaneous requests cannot both start one.
    /// </summary>
    Task<LibraryImportRunItem> CreateOrGetActiveAsync(
        string libraryId,
        string libraryName,
        string mediaType,
        string rootPath,
        CancellationToken cancellationToken);

    Task<LibraryImportRunItem?> GetAsync(string runId, CancellationToken cancellationToken);

    Task<LibraryImportRunItem?> GetActiveForLibraryAsync(string libraryId, CancellationToken cancellationToken);

    Task<LibraryImportRunItem?> GetLatestForLibraryAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs that still have work to do but have had nothing happen to them
    /// since <paramref name="idleBeforeUtc"/> — the worker died mid-slice, or
    /// the process restarted. Paused runs are deliberately excluded: they are
    /// idle because somebody asked them to be.
    /// </summary>
    Task<IReadOnlyList<LibraryImportResumeCandidate>> ListResumableRunsAsync(
        DateTimeOffset idleBeforeUtc,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a queued or paused run to running and records the estimated total
    /// discovered up front. Returns <c>false</c> when the run has since been
    /// cancelled, which is how a cancel mid-slice takes effect.
    /// </summary>
    Task<bool> MarkRunningAsync(string runId, int estimatedTotal, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the position marker and the counters in one statement, so a
    /// crash between two slices can only ever replay a batch that was already
    /// written — never skip one. Replaying is safe because every import write
    /// is an upsert.
    /// </summary>
    Task RecordSliceAsync(
        string runId,
        string? cursor,
        int processedDelta,
        int importedDelta,
        int skippedDelta,
        int deferredDelta,
        IReadOnlyList<string> sampleTitles,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records why a slice failed without changing the run's status, so a run
    /// that is being retried says so instead of looking like it silently
    /// stopped.
    /// </summary>
    Task RecordErrorAsync(string runId, string error, CancellationToken cancellationToken);

    Task<bool> TrySetStatusAsync(
        string runId,
        string status,
        IReadOnlyList<string> allowedCurrentStatuses,
        string? lastError,
        CancellationToken cancellationToken);

    Task RecordIssueAsync(
        string runId,
        string libraryId,
        string sourcePath,
        string kind,
        string detail,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryImportIssueItem>> ListIssuesAsync(
        string runId,
        int take,
        CancellationToken cancellationToken);
}
