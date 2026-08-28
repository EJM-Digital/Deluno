using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The query a lane sleeps on.
///
/// <para>Every executor lane calls this on the tick where it leases nothing, so
/// if it throws, every lane dies on its first idle tick and the queue silently
/// stops draining — which is exactly what happened on the rig.</para>
/// </summary>
public sealed class JobNextDueTests
{
    [Fact]
    public async Task An_empty_queue_has_no_next_due()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        Assert.Null(await store.NextDueUtcAsync(["library.subtitles.scan"], CancellationToken.None));
    }

    [Fact]
    public async Task A_queued_job_reports_when_it_is_due()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        await store.EnqueueAsync(
            new EnqueueJobRequest("library.subtitles.scan", "tv", "{}", null, null),
            CancellationToken.None);

        Assert.NotNull(await store.NextDueUtcAsync(["library.subtitles.scan"], CancellationToken.None));
    }

    [Fact]
    public async Task Another_lane_s_work_is_not_this_lane_s_business()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        await store.EnqueueAsync(
            new EnqueueJobRequest("intake.sync", "intake", "{}", null, null),
            CancellationToken.None);

        Assert.Null(await store.NextDueUtcAsync(["library.subtitles.search"], CancellationToken.None));
    }

    [Fact]
    public async Task No_job_types_is_answered_rather_than_queried()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        Assert.Null(await store.NextDueUtcAsync([], CancellationToken.None));
    }

    /// <summary>
    /// A lane must be able to lease what it was signalled about. This is the
    /// whole loop in miniature: enqueue, then lease as the lane does.
    /// </summary>
    [Fact]
    public async Task A_lane_leases_the_job_it_was_woken_for()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        await store.EnqueueAsync(
            new EnqueueJobRequest("library.import.existing", "library-import", "{}", null, null),
            CancellationToken.None);

        var leased = await store.LeaseBatchAsync(
            "worker-test-import.existing",
            TimeSpan.FromMinutes(2),
            ["library.import.existing"],
            4,
            CancellationToken.None);

        Assert.Single(leased);
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage)
    {
        var timeProvider = TimeProvider.System;
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(
            storage.Factory,
            timeProvider,
            new RecordingRealtimeEventPublisher(),
            new NullDownloadDispatchesRepository());
    }
}
