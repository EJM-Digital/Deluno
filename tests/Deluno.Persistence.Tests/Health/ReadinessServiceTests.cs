using Deluno.Api.Health;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Health;

public sealed class ReadinessServiceTests
{
    [Fact]
    public async Task CheckAsync_reports_not_ready_until_worker_heartbeat_exists()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);

        var readiness = CreateReadiness(storage, timeProvider);
        var result = await readiness.CheckAsync(CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check =>
            check.Name == "worker:heartbeat" &&
            check.Status == "not_ready");
    }

    [Fact]
    public async Task CheckAsync_reports_ready_when_storage_databases_worker_and_queue_are_healthy()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var jobs = await InitializeJobsAsync(storage, timeProvider);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        var readiness = CreateReadiness(storage, timeProvider);
        var result = await readiness.CheckAsync(CancellationToken.None);

        Assert.True(result.Ready);
        Assert.All(result.Checks, check => Assert.Equal("ready", check.Status));
    }

    [Fact]
    public async Task CheckAsync_reports_not_ready_when_queue_contains_stalled_running_jobs()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var jobs = await InitializeJobsAsync(storage, timeProvider);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO job_queue (
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id
                )
                VALUES (
                    'stalled-job', 'library.search', 'test', 'running', NULL, 1, @createdUtc, @scheduledUtc,
                    @startedUtc, NULL, @leasedUntilUtc, 'worker-test', NULL, 'library', 'movies-main'
                );
                """;
            AddParameter(command, "@createdUtc", "2026-04-29T05:00:00Z");
            AddParameter(command, "@scheduledUtc", "2026-04-29T05:00:00Z");
            AddParameter(command, "@startedUtc", "2026-04-29T05:00:00Z");
            AddParameter(command, "@leasedUntilUtc", "2026-04-29T05:58:00Z");
            await command.ExecuteNonQueryAsync();
        }

        var readiness = CreateReadiness(storage, timeProvider);
        var result = await readiness.CheckAsync(CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check =>
            check.Name == "jobs:queue" &&
            check.Status == "not_ready" &&
            (long)check.Details["stalledRunning"]! == 1);
    }

    private static DelunoReadinessService CreateReadiness(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(
            storage.Factory,
            Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
            timeProvider);

    private static async Task<SqliteJobStore> InitializeJobsAsync(
        TestStorage storage,
        TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new JobsSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher());
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
