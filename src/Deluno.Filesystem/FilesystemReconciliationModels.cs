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
    string Message,
    bool LibraryUnreachable = false);

public sealed record TitleRescanMissingFile(string FileName, string Path, bool MarkedMissing);

public sealed record TitleRescanUntrackedFile(string FileName, string Path, long? SizeBytes, string IssueId);

/// <param name="Outcome">ok | changed | not-changed | nothing-tracked | library-unreachable | library-unknown</param>
public sealed record TitleRescanResult(
    string MediaType,
    string TitleId,
    string Title,
    string Outcome,
    int FilesChecked,
    int FilesPresent,
    IReadOnlyList<TitleRescanMissingFile> Missing,
    IReadOnlyList<TitleRescanUntrackedFile> Untracked,
    string? Folder,
    string Summary);
