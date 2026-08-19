namespace Deluno.Contracts;

/// <summary>
/// One run of "bring in the files that are already on disk" for a library.
///
/// Import used to be a single HTTP call that returned when it had finished. At
/// 20,000 items that call runs for hours, so a run is now a tracked operation
/// with a position marker: the request starts it, a worker advances it in
/// slices, and a restart picks it up where it stopped instead of starting over.
/// </summary>
public sealed record LibraryImportRunItem(
    string Id,
    string LibraryId,
    string LibraryName,
    string MediaType,
    string RootPath,
    string Status,
    int EstimatedTotal,
    int ProcessedCount,
    int ImportedCount,
    int SkippedCount,
    int DeferredCount,
    string? Cursor,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    string? LastError,
    IReadOnlyList<string> SampleTitles);

/// <summary>
/// The statuses a run moves through. Kept as strings because they are stored
/// and served as-is, and compared with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> everywhere.
/// </summary>
public static class LibraryImportRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    /// <summary>A run in one of these states still has work left to do.</summary>
    public static readonly IReadOnlyList<string> Active = [Queued, Running, Paused];

    public static bool IsActive(string status)
        => Active.Contains(status, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Something the run deliberately set aside rather than guessing at. A title
/// that cannot be parsed, or a directory that cannot be read, must not halt an
/// import of 20,000 items — it is recorded here and the run carries on.
/// </summary>
public sealed record LibraryImportIssueItem(
    string Id,
    string RunId,
    string LibraryId,
    string SourcePath,
    string Kind,
    string Detail,
    DateTimeOffset CreatedUtc);

/// <summary>
/// A run plus the derived numbers a progress display needs, so every caller
/// does not compute percentages and estimates its own way.
/// </summary>
public sealed record LibraryImportRunProgress(
    LibraryImportRunItem Run,
    int PercentComplete,
    double? ItemsPerSecond,
    int? EstimatedSecondsRemaining)
{
    public static LibraryImportRunProgress From(LibraryImportRunItem run, DateTimeOffset now)
    {
        var percent = run.EstimatedTotal <= 0
            ? (LibraryImportRunStatuses.IsActive(run.Status) ? 0 : 100)
            : (int)Math.Clamp(Math.Round(run.ProcessedCount * 100d / run.EstimatedTotal), 0, 100);

        double? rate = null;
        int? remainingSeconds = null;

        var reference = run.CompletedUtc ?? now;
        if (run.StartedUtc is { } startedUtc && run.ProcessedCount > 0)
        {
            var elapsed = (reference - startedUtc).TotalSeconds;
            if (elapsed >= 1)
            {
                rate = run.ProcessedCount / elapsed;
                var remaining = run.EstimatedTotal - run.ProcessedCount;
                if (remaining > 0 && rate > 0)
                {
                    remainingSeconds = (int)Math.Ceiling(remaining / rate.Value);
                }
            }
        }

        return new LibraryImportRunProgress(run, percent, rate, remainingSeconds);
    }
}
