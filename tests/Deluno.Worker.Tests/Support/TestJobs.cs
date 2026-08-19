using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Tests.Support;

internal static class TestJobs
{
    internal static JobQueueItem Create(
        string jobType,
        string? payloadJson = null,
        string? relatedEntityId = null,
        string? relatedEntityType = null)
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        return new JobQueueItem(
            Id: Guid.NewGuid().ToString("N"),
            JobType: jobType,
            Source: "test",
            Status: "leased",
            PayloadJson: payloadJson,
            Attempts: 0,
            CreatedUtc: now,
            ScheduledUtc: now,
            StartedUtc: now,
            CompletedUtc: null,
            LeasedUntilUtc: now.AddMinutes(2),
            WorkerId: "worker-test",
            LastError: null,
            RelatedEntityType: relatedEntityType,
            RelatedEntityId: relatedEntityId,
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);
    }
}
