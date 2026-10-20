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

    // DOK-07: what the container's health check asks (/api/health/ready) turns unhealthy for a broken database or a data folder that cannot be written.
    [Fact]
    public async Task CheckAsync_reports_not_ready_when_a_database_file_is_damaged()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var jobs = await InitializeJobsAsync(storage, timeProvider);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using (var platform = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            using var command = platform.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllBytes(storage.Factory.GetDatabasePath(DelunoDatabaseNames.Movies), System.Text.Encoding.ASCII.GetBytes(new string('x', 4096)));

        var result = await CreateReadiness(storage, timeProvider).CheckAsync(CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("not_ready", result.Status);
        Assert.Contains(result.Checks, check => check.Name == $"database:{DelunoDatabaseNames.Movies}" && check.Status == "not_ready");
        Assert.Contains(result.Checks, check => check.Name == $"database:{DelunoDatabaseNames.Platform}" && check.Status == "ready");
    }

    [Fact]
    public async Task CheckAsync_reports_not_ready_when_the_data_folder_cannot_be_written()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var jobs = await InitializeJobsAsync(storage, timeProvider);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);
        // A file where the data folder should be: nothing can be written under it, on every operating system.
        var notAFolder = Path.Combine(storage.DataRoot, "not-a-folder");
        File.WriteAllText(notAFolder, "x");
        var readiness = new DelunoReadinessService(storage.Factory, Options.Create(new StoragePathOptions { DataRoot = notAFolder }), timeProvider);

        var result = await readiness.CheckAsync(CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Name == "storage:writable" && check.Status == "not_ready");
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

    [Fact]
    public async Task CheckAsync_reports_ready_when_background_automation_is_paused_with_held_jobs()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        var jobs = await InitializeJobsAsync(storage, timeProvider);
        await jobs.HeartbeatAsync("worker-test", CancellationToken.None);

        await using (var platform = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var setup = platform.CreateCommand();
            setup.CommandText =
                "CREATE TABLE system_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);" +
                "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('jobs.autoStart', 'false', '2026-04-29T06:00:00Z');";
            await setup.ExecuteNonQueryAsync();
        }

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
                    'held-job', 'movies.catalog.refresh', 'test', 'queued', NULL, 0, @createdUtc, @scheduledUtc,
                    NULL, NULL, NULL, NULL, NULL, 'movie', 'movie-1'
                );
                """;
            AddParameter(command, "@createdUtc", "2026-04-29T05:00:00Z");
            AddParameter(command, "@scheduledUtc", "2026-04-29T05:00:00Z");
            await command.ExecuteNonQueryAsync();
        }

        var readiness = CreateReadiness(storage, timeProvider);
        var result = await readiness.CheckAsync(CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Contains(result.Checks, check =>
            check.Name == "jobs:queue" &&
            check.Status == "ready" &&
            check.Details["backgroundAutomationEnabled"] is false);
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

        return new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
