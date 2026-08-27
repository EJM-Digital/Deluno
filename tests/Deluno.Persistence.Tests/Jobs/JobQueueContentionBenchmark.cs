using System.Diagnostics;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The one place every lane meets: the shared <c>jobs</c> database.
///
/// Deluno keeps six SQLite files, so a movie write and a TV write never touch
/// the same one — but every lane leases and completes work in <c>jobs</c>, and
/// SQLite allows one writer per file. With the six lanes at their configured
/// widths that is up to 25 workers converging on a single file, which is the
/// only place TV work can wait behind movie work for a reason that is not a
/// genuinely scarce resource.
///
/// Asked for by James before Subber adds load to it: "I don't want to burn
/// memory or CPU cycles unnecessarily, and routes and functions inside Deluno
/// should not fight for processing power or schedules."
///
/// This measures rather than asserts. The thresholds are deliberately loose —
/// it is a regression guard against a change that makes queue contention an
/// order of magnitude worse, not a benchmark to tune against. The numbers it
/// prints are the point; run it with
/// <c>dotnet test --filter JobQueueContentionBenchmark -l "console;verbosity=detailed"</c>.
/// </summary>
public sealed class JobQueueContentionBenchmark(ITestOutputHelper output)
{
    /// The six lanes at their real widths: import 8, catalog 8, metadata 4,
    /// search 2, intake 2, planning 1.
    private const int Workers = 25;
    private const int JobsPerWorker = 40;

    [Fact]
    public async Task Every_lane_leasing_at_once_does_not_starve_any_of_them()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteJobStore(
            storage.Factory, clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        // One job type per lane, so starvation of any single lane is visible
        // rather than averaged away.
        string[] laneTypes =
        [
            "filesystem.import.execute",
            "movies.quality.recalculate",
            "movies.metadata.refresh",
            "library.search",
            "intake.sync",
            "series.catalog.refresh"
        ];

        var total = Workers * JobsPerWorker;
        for (var i = 0; i < total; i++)
        {
            await store.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: laneTypes[i % laneTypes.Length],
                    Source: "benchmark",
                    PayloadJson: $$"""{"n":{{i}}}""",
                    ScheduledUtc: null,
                    RelatedEntityType: "benchmark",
                    RelatedEntityId: $"item-{i}"),
                CancellationToken.None);
        }

        var drained = new int[laneTypes.Length];
        var stopwatch = Stopwatch.StartNew();

        // Every worker leases and completes as fast as it can, all against the
        // one file, which is the contention this exists to expose.
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(async worker =>
        {
            var laneIndex = worker % laneTypes.Length;
            var jobType = laneTypes[laneIndex];

            while (true)
            {
                var batch = await store.LeaseBatchAsync(
                    $"bench-{worker}", TimeSpan.FromMinutes(1), [jobType], 4, CancellationToken.None);
                if (batch.Count == 0)
                {
                    return;
                }

                foreach (var job in batch)
                {
                    await store.CompleteAsync(job.Id, $"bench-{worker}", null, CancellationToken.None);
                    Interlocked.Increment(ref drained[laneIndex]);
                }
            }
        }));

        stopwatch.Stop();

        var perSecond = total / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        output.WriteLine($"{total} jobs through {Workers} concurrent workers in {stopwatch.ElapsedMilliseconds} ms");
        output.WriteLine($"{perSecond:F0} lease+complete round trips per second against one SQLite file");
        for (var i = 0; i < laneTypes.Length; i++)
        {
            output.WriteLine($"  {laneTypes[i],-28} {drained[i]} drained");
        }

        // Every lane emptied. A lane left holding work while others finished
        // would be the starvation this is looking for.
        Assert.Equal(total, drained.Sum());

        // Loose on purpose: a regression guard, not a target. Anything slower
        // than this means the queue has become the bottleneck rather than the
        // work, and Subber must not be what pushes it there.
        Assert.True(
            perSecond > 100,
            $"Job queue managed only {perSecond:F0} lease+complete round trips per second across {Workers} workers.");
    }
}
