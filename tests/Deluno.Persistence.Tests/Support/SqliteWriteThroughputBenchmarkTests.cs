using System.Collections.Concurrent;
using System.Diagnostics;
using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Support;

public sealed class SqliteWriteThroughputBenchmarkTests
{
    private static readonly string[] DatabaseNames =
    [
        DelunoDatabaseNames.Platform,
        DelunoDatabaseNames.Movies,
        DelunoDatabaseNames.Series,
        DelunoDatabaseNames.Jobs,
        DelunoDatabaseNames.Cache
    ];

    private static readonly int[] WriterCounts = [1, 2, 4, 8, 16, 24];
    private const int DurationSeconds = 10;
    private const int BatchSize = 100;
    private const int LatencySampleLimit = 100_000;

    [SqliteBenchmarkFact]
    public async Task SqliteWriteThroughput()
    {
        using var storage = TestStorage.Create();
        foreach (var databaseName in DatabaseNames)
        {
            await PrepareBenchmarkTableAsync(storage.Factory, databaseName);
        }

        var results = new List<BenchmarkResult>(DatabaseNames.Length * WriterCounts.Length * 2);
        foreach (var databaseName in DatabaseNames)
        {
            foreach (var writerCount in WriterCounts)
            {
                foreach (var batched in new[] { false, true })
                {
                    var result = await MeasureAsync(storage.Factory, databaseName, writerCount, batched);
                    results.Add(result);
                    Console.WriteLine(
                        $"{result.DatabaseName,-8} {result.WriterCount,2} {(result.Batched ? "batched" : "single "),-7} " +
                        $"{result.RowsPerSecond,10:N0} rows/s p50 {result.P50CommitMilliseconds,8:N3} ms " +
                        $"p99 {result.P99CommitMilliseconds,8:N3} ms busy {result.BusyCount,4}");
                }
            }
        }

        Assert.Equal(DatabaseNames.Length * WriterCounts.Length * 2, results.Count);
        Assert.All(results, result => Assert.True(result.RowsPerSecond > 0, $"{result.DatabaseName} produced no committed rows."));
    }

    private static async Task PrepareBenchmarkTableAsync(
        SqliteDatabaseConnectionFactory factory,
        string databaseName)
    {
        await using var connection = await factory.OpenConnectionAsync(databaseName, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS deluno_write_benchmark (
                id TEXT PRIMARY KEY,
                payload TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<BenchmarkResult> MeasureAsync(
        SqliteDatabaseConnectionFactory factory,
        string databaseName,
        int writerCount,
        bool batched)
    {
        await using (var cleanupConnection = await factory.OpenConnectionAsync(databaseName, CancellationToken.None))
        {
            using var cleanup = cleanupConnection.CreateCommand();
            cleanup.CommandText = "DELETE FROM deluno_write_benchmark;";
            await cleanup.ExecuteNonQueryAsync();
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(DurationSeconds));
        long rows = 0;
        long busyCount = 0;
        var latencySamples = new ConcurrentBag<double>();
        var sampleCount = 0;

        void RecordLatency(long started)
        {
            var number = Interlocked.Increment(ref sampleCount);
            if (number <= LatencySampleLimit)
            {
                latencySamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        async Task WriterAsync()
        {
            try
            {
                await using var connection = await factory.OpenConnectionAsync(databaseName, cancellation.Token);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO deluno_write_benchmark (id, payload, created_utc) VALUES (@id, @payload, @createdUtc);";
                var id = command.CreateParameter();
                id.ParameterName = "@id";
                command.Parameters.Add(id);
                var payload = command.CreateParameter();
                payload.ParameterName = "@payload";
                payload.Value = "benchmark";
                command.Parameters.Add(payload);
                var createdUtc = command.CreateParameter();
                createdUtc.ParameterName = "@createdUtc";
                command.Parameters.Add(createdUtc);

                while (!cancellation.IsCancellationRequested)
                {
                    if (!batched)
                    {
                        var started = Stopwatch.GetTimestamp();
                        id.Value = Guid.CreateVersion7().ToString("N");
                        createdUtc.Value = DateTimeOffset.UtcNow.ToString("O");
                        try
                        {
                            await command.ExecuteNonQueryAsync(cancellation.Token);
                            Interlocked.Increment(ref rows);
                            RecordLatency(started);
                        }
                        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                        {
                            Interlocked.Increment(ref busyCount);
                        }

                        continue;
                    }

                    var batchRows = 0;
                    var batchStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        await using var transaction = await connection.BeginTransactionAsync(cancellation.Token);
                        command.Transaction = transaction;
                        for (var index = 0; index < BatchSize && !cancellation.IsCancellationRequested; index++)
                        {
                            id.Value = Guid.CreateVersion7().ToString("N");
                            createdUtc.Value = DateTimeOffset.UtcNow.ToString("O");
                            await command.ExecuteNonQueryAsync(cancellation.Token);
                            batchRows++;
                        }

                        await transaction.CommitAsync(cancellation.Token);
                        command.Transaction = null;
                        Interlocked.Add(ref rows, batchRows);
                        RecordLatency(batchStarted);
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                    {
                        command.Transaction = null;
                        Interlocked.Increment(ref busyCount);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        await Task.WhenAll(Enumerable.Range(0, writerCount).Select(_ => WriterAsync()));
        var orderedLatencies = latencySamples.OrderBy(value => value).ToArray();
        var duration = DurationSeconds;
        return new BenchmarkResult(
            databaseName,
            writerCount,
            batched,
            rows / (double)duration,
            Percentile(orderedLatencies, 0.50),
            Percentile(orderedLatencies, 0.99),
            busyCount);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }

    private sealed record BenchmarkResult(
        string DatabaseName,
        int WriterCount,
        bool Batched,
        double RowsPerSecond,
        double P50CommitMilliseconds,
        double P99CommitMilliseconds,
        long BusyCount);
}

/// <summary>
/// A measurement, not a gate: it runs when asked for and is skipped otherwise.
///
/// <para><b>Why a benchmark must not be a CI assertion.</b> These compare
/// elapsed wall-clock against a number, and a shared runner is not a stopwatch.
/// A machine that is merely busy fails them, so left in the gate they go red
/// for reasons no one can act on, and a test that fails for reasons no one can
/// act on stops being read. Deluno.Persistence.Tests was already carrying three
/// of these; two were behind this attribute and one was not, and the one that
/// was not is a test that failed twice in four runs under load.</para>
///
/// <para>Nothing is lost by skipping them, because none of them is the only
/// guard on its claim — the regressions they describe are asserted next door
/// against a query plan, which has no clock in it and cannot flake.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SqliteBenchmarkFactAttribute : FactAttribute
{
    public SqliteBenchmarkFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DELUNO_RUN_SQLITE_BENCHMARK"), "1", StringComparison.Ordinal))
        {
            // Named the class rather than one particular benchmark: three
            // classes wear this attribute now, and the message used to send
            // everyone to SqliteWriteThroughput whichever one they had hit.
            Skip = "Benchmark; set DELUNO_RUN_SQLITE_BENCHMARK=1 and filter to the benchmark class you want to read.";
        }
    }
}
