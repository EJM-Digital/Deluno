namespace Deluno.Filesystem;

public sealed record FilesystemReconciliationReport(
    DateTimeOffset ScannedUtc,
    int LibraryCount,
    int IssueCount,
    IReadOnlyList<FilesystemReconciliationIssue> Issues);

public sealed record FilesystemReconciliationIssue(
    string Id,
    string Kind,
    string Severity,
    string MediaType,
    string LibraryId,
    string LibraryName,
    string Path,
    string Title,
    string Summary,
    string RecommendedAction,
    IReadOnlyList<string> RepairActions,
    string? EntityId = null,
    string? EpisodeId = null,
    long? ExpectedSizeBytes = null,
    long? ActualSizeBytes = null);

public sealed record FilesystemReconciliationRepairRequest(
    string IssueId,
    string Action);

public sealed record FilesystemReconciliationRepairResult(
    bool Repaired,
    string Action,
    string Message);
