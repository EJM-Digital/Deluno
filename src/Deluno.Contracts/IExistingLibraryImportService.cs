namespace Deluno.Contracts;

/// <summary>
/// Brings the files already sitting in a library folder into the catalogue.
///
/// This is a tracked background operation, not a request that returns when the
/// work is done. A 20,000-item library takes far longer than any HTTP request
/// should live, so <see cref="StartAsync"/> only creates the run; a worker then
/// advances it one slice at a time through <see cref="RunSliceAsync"/>, and the
/// run carries a position marker so a restart continues instead of starting
/// over.
/// </summary>
public interface IExistingLibraryImportService
{
    /// <summary>
    /// Reads one bounded page of existing top-level files and folders without
    /// changing the catalogue. The caller must review and select the returned
    /// paths before anything is imported.
    /// </summary>
    Task<ExistingLibraryPreviewPage?> PreviewAsync(
        string libraryId,
        string? cursor,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Imports only the paths the user explicitly selected from a preview page.
    /// Paths are checked against the library root again before they are read.
    /// </summary>
    Task<ExistingLibraryImportResult?> ImportSelectedAsync(
        string libraryId,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts an import for the library, or returns the one already in flight.
    /// Returns <c>null</c> when the library does not exist or its root path is
    /// not readable.
    /// </summary>
    Task<LibraryImportRunProgress?> StartAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// The run in flight for this library, or the most recent finished one.
    /// </summary>
    Task<LibraryImportRunProgress?> GetProgressAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the active run to <paramref name="desiredStatus"/> — one of
    /// paused, running (resume) or cancelled. Returns <c>null</c> when there is
    /// no active run, or when the transition is not allowed from where the run
    /// currently is.
    /// </summary>
    Task<LibraryImportRunProgress?> SetStateAsync(string libraryId, string desiredStatus, CancellationToken cancellationToken);

    /// <summary>
    /// What the run set aside for review rather than guessing at.
    /// </summary>
    Task<IReadOnlyList<LibraryImportIssueItem>> ListIssuesAsync(string libraryId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Advances one run by a bounded slice of work and returns where it got to.
    /// Called by the worker; the slice is sized to finish well inside a job
    /// lease so a long import never looks like a stalled worker.
    /// </summary>
    Task<LibraryImportSliceOutcome> RunSliceAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs that should be advanced but have had nothing happen to them for a
    /// while — the worker died mid-slice, or the process restarted. Bounded by
    /// <paramref name="take"/>, and there is at most one active run per library.
    /// </summary>
    Task<IReadOnlyList<LibraryImportResumeCandidate>> ListResumableRunsAsync(
        DateTimeOffset idleBeforeUtc,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>Where a slice got to, and whether the run has more to do.</summary>
public sealed record LibraryImportSliceOutcome(
    string RunStatus,
    int ProcessedInSlice,
    int ProcessedTotal,
    bool MoreWorkRemains,
    string Message)
{
    /// <summary>
    /// The dedupe key a continuation job should carry. Including the position
    /// makes each continuation distinct from the job currently running, while
    /// still collapsing a continuation and a resume sweep that both decide the
    /// same slice is next.
    /// </summary>
    public static string ContinuationDedupeKey(string runId, int processedTotal)
        => $"library.import.existing:{runId}:{processedTotal}";
}

public sealed record LibraryImportResumeCandidate(
    string RunId,
    string LibraryId,
    string LibraryName,
    int ProcessedCount);

public sealed record ExistingLibraryPreviewPage(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string RootPath,
    IReadOnlyList<ExistingLibraryCandidate> Items,
    string? NextCursor,
    bool HasMore);

public sealed record ExistingLibraryCandidate(
    string SourcePath,
    string RelativePath,
    string Title,
    int? Year,
    string? DetectedQuality,
    long? FileSizeBytes,
    bool IsDirectory,
    bool CanImport,
    string? IssueKind,
    string? IssueDetail);

public sealed record ExistingLibraryImportResult(
    int RequestedCount,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<ExistingLibraryImportIssue> Issues);

public sealed record ExistingLibraryImportIssue(
    string SourcePath,
    string Kind,
    string Detail);
