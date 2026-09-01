using Deluno.Platform.Migration;

namespace Deluno.Platform.Contracts;

public sealed record MigrationReport(
    string SourceKind,
    string SourceName,
    bool Valid,
    MigrationReportSummary Summary,
    IReadOnlyList<MigrationReportOperation> Operations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    MigrationReportInventory? Inventory = null);

public sealed record MigrationReportSummary(
    int CreateCount,
    int SkipCount,
    int ConflictCount,
    int UnsupportedCount,
    int WarningCount,
    int TitleCount,
    int MonitoredCount,
    int WantedCount);

public sealed record MigrationReportOperation(
    string Id,
    string Category,
    string TargetType,
    string Name,
    string Action,
    bool CanApply,
    string Reason,
    IReadOnlyDictionary<string, string?> Data,
    IReadOnlyList<string> Warnings);

public sealed record MigrationApplyResponse(
    MigrationReport Report,
    IReadOnlyList<MigrationAppliedItem> Applied,
    string? AuditReportId = null,
    MigrationBackupReceipt? Backup = null);

public sealed record MigrationAppliedItem(
    string OperationId,
    string TargetType,
    string Name,
    string CreatedId,
    string Result);
