namespace Deluno.Intake.Contracts;

/// <summary>
/// Read-only result of fetching an import list. Preview is deliberately separate
/// from sync: it never writes a title, queues a search, or changes the list's
/// last-sync state.
/// </summary>
public sealed record IntakeListPreviewResult(
    string SourceId,
    string SourceName,
    string Provider,
    string MediaType,
    string? TargetLibraryName,
    int FetchedCount,
    int ShownCount,
    bool IsTruncated,
    IReadOnlyList<IntakeListPreviewItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record IntakeListPreviewItem(
    string Title,
    int? Year,
    string MediaType,
    string? ImdbId,
    string Action,
    string Reason,
    string MatchConfidence,
    string? ExclusionId = null);

/// <summary>
/// A user-approved entry from a read-only preview. The stable external ID is
/// preferred; title/year is retained only for providers that cannot supply one.
/// </summary>
public sealed record IntakeListEntrySelection(
    string Title,
    int? Year,
    string? ImdbId);

public sealed record ApproveIntakeListPreviewRequest(
    IReadOnlyList<IntakeListEntrySelection> Entries,
    bool SearchAfterAdd);

public sealed record IntakeListApprovalResult(
    int SelectedCount,
    int MatchedCount,
    int AddedCount,
    int DuplicateCount,
    int SkippedCount,
    int ErrorCount,
    bool SearchRequested,
    string Summary);

public interface IIntakeListPreviewService
{
    Task<IntakeListPreviewResult> PreviewAsync(string sourceId, CancellationToken cancellationToken);
}

public interface IIntakeListApprovalService
{
    Task<IntakeListApprovalResult> ApproveAsync(
        string sourceId,
        ApproveIntakeListPreviewRequest request,
        CancellationToken cancellationToken);
}
