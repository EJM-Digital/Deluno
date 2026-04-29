using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class JobStoreTests
{
    [Fact]
    public async Task Enqueue_lease_complete_and_retry_failed_jobs_preserve_expected_lifecycle()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher());

        var queued = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "library.search",
                Source: "test",
                PayloadJson: """{"libraryId":"movies-main","libraryName":"Movies","mediaType":"movie"}""",
                ScheduledUtc: null,
                RelatedEntityType: "library",
                RelatedEntityId: "movies-main"),
            CancellationToken.None);

        var leased = await store.LeaseNextAsync(
            workerId: "worker-a",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);

        Assert.NotNull(leased);
        Assert.Equal(queued.Id, leased.Id);
        Assert.Equal("running", leased.Status);
        Assert.Equal(1, leased.Attempts);
        Assert.Equal("worker-a", leased.WorkerId);

        var noDuplicateLease = await store.LeaseNextAsync(
            workerId: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);
        Assert.Null(noDuplicateLease);

        await store.FailAsync(queued.Id, "worker-a", "Indexer timed out.", CancellationToken.None);
        Assert.Equal(1, await store.RetryFailedAsync(CancellationToken.None));

        var retried = await store.LeaseNextAsync(
            workerId: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(queued.Id, retried.Id);
        Assert.Equal(2, retried.Attempts);

        await store.CompleteAsync(queued.Id, "worker-b", "Library search completed.", CancellationToken.None);

        var stored = Assert.Single(await store.ListAsync(10, CancellationToken.None));
        Assert.Equal("completed", stored.Status);
        Assert.NotNull(stored.CompletedUtc);
        Assert.Null(stored.LeasedUntilUtc);
        Assert.Null(stored.LastError);
    }
}
