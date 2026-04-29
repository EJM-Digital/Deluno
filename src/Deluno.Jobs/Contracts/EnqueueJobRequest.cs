namespace Deluno.Jobs.Contracts;

public sealed record EnqueueJobRequest(
    string JobType,
    string Source,
    string? PayloadJson,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset? ScheduledUtc = null,
    string? IdempotencyKey = null,
    string? DedupeKey = null,
    int? MaxAttempts = null);
