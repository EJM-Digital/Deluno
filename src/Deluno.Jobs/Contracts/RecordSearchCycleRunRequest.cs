namespace Deluno.Jobs.Contracts;

public sealed record RecordSearchCycleRunRequest(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string TriggerKind,
    string Status,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    string? NotesJson,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc);
