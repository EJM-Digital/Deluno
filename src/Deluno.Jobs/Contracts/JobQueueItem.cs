namespace Deluno.Jobs.Contracts;

public sealed record JobQueueItem(
    string Id,
    string JobType,
    string Source,
    string Status,
    string? PayloadJson,
    int Attempts,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ScheduledUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? LeasedUntilUtc,
    string? WorkerId,
    string? LastError,
    string? RelatedEntityType,
    string? RelatedEntityId);
