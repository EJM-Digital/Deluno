using Deluno.Jobs.Contracts;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

public sealed class RepeatedDispatchImportIdentityTests
{
    private const string SourcePath = "C:\\completed\\Release";

    [Fact]
    public void Older_completed_import_does_not_reserve_a_reused_source_for_a_new_dispatch()
    {
        var oldJob = ImportJob("completed", "dispatch-old");
        var newDispatch = new DispatchCatalogueLink("dispatch-new", "movie", "movie-1");

        Assert.False(WorkPlanner.HasImportReservation([oldJob], "C:/completed/Release", newDispatch));
        Assert.True(WorkPlanner.HasImportReservation(
            [ImportJob("completed", "dispatch-new")],
            "C:/completed/Release",
            newDispatch));
    }

    [Fact]
    public void Dead_letter_does_not_reserve_a_dispatch_but_unscoped_imports_keep_source_dedupe()
    {
        var dispatch = new DispatchCatalogueLink("dispatch-new", "movie", "movie-1");

        Assert.False(WorkPlanner.HasImportReservation(
            [ImportJob("dead-letter", "dispatch-new")],
            "C:/completed/Release",
            dispatch));
        Assert.True(WorkPlanner.HasImportReservation(
            [ImportJob("completed", null)],
            "C:/completed/Release",
            dispatch: null));
    }

    private static JobQueueItem ImportJob(string status, string? dispatchId)
        => new(
            Id: $"job-{status}-{dispatchId}",
            JobType: "filesystem.import.execute",
            Source: "download-client",
            Status: status,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                Preview = new { SourcePath },
                DispatchId = dispatchId
            }),
            Attempts: 1,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ScheduledUtc: DateTimeOffset.UnixEpoch,
            StartedUtc: null,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: "movie",
            RelatedEntityId: "movie-1",
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);
}
