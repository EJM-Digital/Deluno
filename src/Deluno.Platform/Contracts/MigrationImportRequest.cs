namespace Deluno.Platform.Contracts;

public sealed record MigrationImportRequest(
    string? SourceKind,
    string? SourceName,
    string? PayloadJson);
