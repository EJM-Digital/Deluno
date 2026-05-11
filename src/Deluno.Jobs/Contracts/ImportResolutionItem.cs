namespace Deluno.Jobs.Contracts;

public sealed record ImportResolutionItem(
    string Id,
    string DispatchId,
    string EntityId,
    string MediaType,
    string LibraryId,
    string Status,
    string? FilePath,
    string? FileName,
    long? FileSize,
    DateTimeOffset? ImportedUtc,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? FailedUtc);
