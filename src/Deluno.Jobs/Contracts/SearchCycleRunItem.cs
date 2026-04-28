namespace Deluno.Jobs.Contracts;

public sealed record SearchCycleRunItem(
    string Id,
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
