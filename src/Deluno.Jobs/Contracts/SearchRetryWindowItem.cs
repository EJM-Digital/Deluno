namespace Deluno.Jobs.Contracts;

public sealed record SearchRetryWindowItem(
    string EntityType,
    string EntityId,
    string LibraryId,
    string MediaType,
    string ActionKind,
    DateTimeOffset NextEligibleUtc,
    DateTimeOffset LastAttemptUtc,
    int AttemptCount,
    string? LastResult,
    DateTimeOffset UpdatedUtc);
