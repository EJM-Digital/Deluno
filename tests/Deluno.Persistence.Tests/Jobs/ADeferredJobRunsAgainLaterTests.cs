using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #598: a job that could not do its work yet goes back in the queue, runs
/// again later, and is not counted as a failed attempt.
///
/// <para><b>Why this file exists.</b> Sixty episode searches queued together
/// share each indexer's pace. The ones that waited too long recorded a search
/// nobody had answered and waited an hour. A deferral hands back the attempt
/// its lease took: without that, a job deferred three times would never be
/// leased again, because leasing stops at the attempt limit.</para>
/// </summary>
public sealed class ADeferredJobRunsAgainLaterTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-23T12:43:00Z");
    private const string Worker = "worker-test-search.episodes";

    [Fact]
    public async Task A_deferred_job_waits_its_delay_and_keeps_its_attempts()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var store = await CreateStoreAsync(storage, clock);
        var job = await store.EnqueueAsync(
            new EnqueueJobRequest("episode.search", "series", "{}", "episode", "e-201", MaxAttempts: 3),
            CancellationToken.None);

        // Deferred more times than the attempt limit allows failures.
        for (var round = 0; round < 5; round++)
        {
            var leased = Assert.Single(await store.LeaseBatchAsync(Worker, TimeSpan.FromMinutes(2), ["episode.search"], 4, CancellationToken.None));
            Assert.Equal(job.Id, leased.Id);
            await store.DeferAsync(job.Id, Worker, clock.GetUtcNow().AddMinutes(3), CancellationToken.None);

            Assert.Empty(await store.LeaseBatchAsync(Worker, TimeSpan.FromMinutes(2), ["episode.search"], 4, CancellationToken.None));
            clock.Advance(TimeSpan.FromMinutes(3));
        }

        var stored = (await store.ListAsync(10, CancellationToken.None)).Single(item => item.Id == job.Id);
        Assert.Equal("queued", stored.Status);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Single(await store.LeaseBatchAsync(Worker, TimeSpan.FromMinutes(2), ["episode.search"], 4, CancellationToken.None));
    }

    /// <summary>A lease that has since passed to another worker is not handed back by the first.</summary>
    [Fact]
    public async Task Only_the_worker_holding_the_lease_can_defer_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var store = await CreateStoreAsync(storage, clock);
        var job = await store.EnqueueAsync(new EnqueueJobRequest("episode.search", "series", "{}", null, null), CancellationToken.None);
        await store.LeaseBatchAsync(Worker, TimeSpan.FromMinutes(2), ["episode.search"], 4, CancellationToken.None);

        await store.DeferAsync(job.Id, "some-other-worker", clock.GetUtcNow().AddMinutes(3), CancellationToken.None);

        Assert.Equal("running", (await store.ListAsync(10, CancellationToken.None)).Single(item => item.Id == job.Id).Status);
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage, TimeProvider clock)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(
            storage.Factory,
            clock,
            new RecordingRealtimeEventPublisher(),
            new NullDownloadDispatchesRepository());
    }
}
