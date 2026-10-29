using Deluno.Api.Health;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Health;

/// <summary>Low-severity rows (#1329) of the readiness checks the container health check asks, on real SQLite databases and a real data folder.</summary>
public sealed class ReadinessLowGapTests
{
    private static async Task<(SqliteJobStore Jobs, FixedTimeProvider Time, DelunoReadinessService Readiness)> StartAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var readiness = new DelunoReadinessService(storage.Factory, Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), time);
        return (jobs, time, readiness);
    }

    [Fact]
    public async Task A_worker_heartbeat_is_fresh_up_to_forty_five_seconds_and_stale_beyond_that_and_says_its_age()
    {
        using var storage = TestStorage.Create();
        var (jobs, time, readiness) = await StartAsync(storage);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(45));
        var fresh = (await readiness.CheckAsync(CancellationToken.None)).Checks.Single(check => check.Name == "worker:heartbeat");
        Assert.Equal("ready", fresh.Status);
        Assert.Equal(45.0, (double)fresh.Details["ageSeconds"]!);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await readiness.CheckAsync(CancellationToken.None);
        var stale = result.Checks.Single(check => check.Name == "worker:heartbeat");
        Assert.False(result.Ready);
        Assert.Equal("not_ready", stale.Status);
        Assert.Equal(46.0, (double)stale.Details["ageSeconds"]!);
    }

    [Fact]
    public async Task A_job_queued_more_than_fifteen_minutes_past_its_time_is_lagged_and_makes_the_queue_not_ready()
    {
        using var storage = TestStorage.Create();
        var (jobs, time, readiness) = await StartAsync(storage);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        async Task QueueAsync(string id, string scheduledUtc)
        {
            await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO job_queue (id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc, started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id)
                VALUES (@id, 'library.search', 'test', 'queued', NULL, 0, @at, @at, NULL, NULL, NULL, NULL, NULL, 'library', 'movies-main');
                """;
            foreach (var (name, value) in new[] { ("@id", id), ("@at", scheduledUtc) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync();
        }

        // Fourteen minutes late is still on time.
        await QueueAsync("late-ish", "2026-04-29T05:46:00Z");
        var onTime = (await readiness.CheckAsync(CancellationToken.None)).Checks.Single(check => check.Name == "jobs:queue");
        Assert.Equal("ready", onTime.Status);
        Assert.Equal(0L, (long)onTime.Details["laggedQueued"]!);

        // Sixteen minutes late is lagged.
        await QueueAsync("lagged", "2026-04-29T05:44:00Z");
        time.Advance(TimeSpan.Zero);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);
        var result = await readiness.CheckAsync(CancellationToken.None);
        var lagged = result.Checks.Single(check => check.Name == "jobs:queue");
        Assert.False(result.Ready);
        Assert.Equal("not_ready", lagged.Status);
        Assert.Equal(1L, (long)lagged.Details["laggedQueued"]!);
    }

    [Fact]
    public async Task The_storage_check_writes_and_removes_its_probe_file_in_the_data_folder_and_leaves_nothing_behind()
    {
        using var storage = TestStorage.Create();
        var (jobs, _, readiness) = await StartAsync(storage);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        var check = (await readiness.CheckAsync(CancellationToken.None)).Checks.Single(item => item.Name == "storage:writable");

        Assert.Equal("ready", check.Status);
        Assert.Empty(Directory.GetFiles(storage.DataRoot, ".deluno-readiness-*"));

        var notAFolder = Path.Combine(storage.DataRoot, "not-a-folder");
        File.WriteAllText(notAFolder, "x");
        var broken = new DelunoReadinessService(storage.Factory, Options.Create(new StoragePathOptions { DataRoot = notAFolder }), new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z")));
        var refused = (await broken.CheckAsync(CancellationToken.None)).Checks.Single(item => item.Name == "storage:writable");
        Assert.Equal("not_ready", refused.Status);
        Assert.StartsWith("Storage root is not writable:", refused.Message);
    }
}
