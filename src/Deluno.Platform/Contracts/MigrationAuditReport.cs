namespace Deluno.Platform.Contracts;

/// <summary>
/// Immutable, redacted evidence of a migration apply. Previewing a migration
/// deliberately creates no record; an apply writes an audit record whether it
/// completes or stops at a recoverable stage failure, so operators can review
/// exactly what happened before retrying.
/// </summary>
public sealed record MigrationAuditReport(
    string Id,
    string SourceKind,
    string SourceName,
    DateTimeOffset AppliedUtc,
    MigrationReport PreflightReport,
    MigrationReport ResultReport,
    IReadOnlyList<MigrationAppliedItem> Applied);
