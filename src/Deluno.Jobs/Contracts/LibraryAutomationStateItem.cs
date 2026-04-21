namespace Deluno.Jobs.Contracts;

public sealed record LibraryAutomationStateItem(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string Status,
    bool SearchRequested,
    DateTimeOffset? LastPlannedUtc,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? NextSearchUtc,
    string? LastJobId,
    string? LastError,
    DateTimeOffset UpdatedUtc);
