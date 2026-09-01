using System.Diagnostics;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Measures the telemetry write cost for a representative full indexer fan-out.
/// Run with <c>DELUNO_RUN_SQLITE_BENCHMARK=1</c> and filter this class.
/// </summary>
public sealed class IndexerQueryStatsBenchmarkTests(ITestOutputHelper output)
{
    private const int IndexersPerCycle = 16;
    private const int WarmupCycles = 2;
    private const int MeasuredCycles = 25;

    [SqliteBenchmarkFact]
    public async Task Full_search_cycle_telemetry_batch_cost()
    {
        var batched = await MeasureAsync(batched: true);
        var oneRowCalls = await MeasureAsync(batched: false);

        output.WriteLine(
            $"Indexer telemetry ({IndexersPerCycle} indexers x {MeasuredCycles} cycles): " +
            $"batched={batched.ElapsedMilliseconds:N1} ms, " +
            $"one-row-calls={oneRowCalls.ElapsedMilliseconds:N1} ms, " +
            $"speedup={(oneRowCalls.ElapsedMilliseconds / Math.Max(batched.ElapsedMilliseconds, 0.01)):N1}x");

        Assert.Equal(IndexersPerCycle * MeasuredCycles, batched.RowsWritten);
        Assert.Equal(oneRowCalls.RowsWritten, batched.RowsWritten);
        Assert.True(batched.ElapsedMilliseconds > 0);
        Assert.True(oneRowCalls.ElapsedMilliseconds > 0);
    }

    private static async Task<BenchmarkResult> MeasureAsync(bool batched)
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-30T12:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteIndexerQueryStatsRepository(storage.Factory);
        var entries = Enumerable.Range(0, IndexersPerCycle)
            .Select(index => new IndexerQueryLogEntry(
                IndexerId: $"indexer-{index}",
                IndexerName: $"Indexer {index}",
                QueryText: "A representative title",
                Categories: "2000,5000",
                MediaType: index % 2 == 0 ? "movies" : "series",
                QueryKind: index % 3 == 0 ? "rss" : "search",
                Outcome: index % 5 == 0 ? "no_results" : "matched",
                ElapsedMilliseconds: 100 + index,
                CandidateCount: index % 4,
                CreatedUtc: now))
            .ToArray();

        for (var cycle = 0; cycle < WarmupCycles; cycle++)
        {
            await RecordCycleAsync(repository, entries, batched, CancellationToken.None);
        }

        var clock = Stopwatch.StartNew();
        for (var cycle = 0; cycle < MeasuredCycles; cycle++)
        {
            await RecordCycleAsync(repository, entries, batched, CancellationToken.None);
        }

        var snapshot = await repository.GetScoreboardAsync(
            now.AddMinutes(-1),
            now.AddMinutes(1),
            CancellationToken.None);
        return new BenchmarkResult(clock.Elapsed.TotalMilliseconds, snapshot.TotalQueries - (long)IndexersPerCycle * WarmupCycles);
    }

    private static async Task RecordCycleAsync(
        IIndexerQueryStatsRepository repository,
        IReadOnlyList<IndexerQueryLogEntry> entries,
        bool batched,
        CancellationToken cancellationToken)
    {
        if (batched)
        {
            await repository.RecordBatchAsync(entries, cancellationToken);
            return;
        }

        foreach (var entry in entries)
        {
            await repository.RecordBatchAsync([entry], cancellationToken);
        }
    }

    private sealed record BenchmarkResult(double ElapsedMilliseconds, long RowsWritten);
}
