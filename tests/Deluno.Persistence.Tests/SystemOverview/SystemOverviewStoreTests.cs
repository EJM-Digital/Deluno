using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>#1098: the small records behind the System screen that the machine cannot give: free space day by day, starts, and the job queue's day.</summary>
public sealed class SystemOverviewStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", CultureInfo.InvariantCulture);

    private static async Task<(TestStorage Storage, SqliteSystemOverviewStore Store)> CreateAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, new SqliteSystemOverviewStore(storage.Factory, new FixedTimeProvider(Now)));
    }

    [Fact]
    public async Task The_last_free_space_reading_of_a_day_replaces_the_earlier_one()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;
        var day = new DateOnly(2026, 10, 1);

        await store.RecordDriveSpaceAsync("D:", day, 900, 1_000, CancellationToken.None);
        await store.RecordDriveSpaceAsync("D:", day, 850, 1_000, CancellationToken.None);

        var row = Assert.Single(await store.ListDriveSpaceAsync(day, CancellationToken.None));
        Assert.Equal(new DriveSpaceDay("D:", day, 850, 1_000), row);
    }

    [Fact]
    public async Task Free_space_comes_back_per_drive_and_only_from_the_days_asked_for()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;
        await store.RecordDriveSpaceAsync("D:", new DateOnly(2026, 9, 20), 900, 1_000, CancellationToken.None);
        await store.RecordDriveSpaceAsync("D:", new DateOnly(2026, 9, 28), 800, 1_000, CancellationToken.None);
        await store.RecordDriveSpaceAsync("D:", new DateOnly(2026, 9, 30), 700, 1_000, CancellationToken.None);
        await store.RecordDriveSpaceAsync("C:", new DateOnly(2026, 9, 30), 100, 500, CancellationToken.None);

        var rows = await store.ListDriveSpaceAsync(new DateOnly(2026, 9, 24), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["C:", "D:", "D:"], rows.Select(row => row.VolumeKey));
        Assert.Equal([new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 30)], rows.Where(row => row.VolumeKey == "D:").Select(row => row.Day));
    }

    [Fact]
    public async Task Starts_in_the_last_seven_days_are_counted_and_a_start_is_recorded_once()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;
        await store.RecordStartAsync(Now.AddDays(-20), CancellationToken.None);
        await store.RecordStartAsync(Now.AddDays(-6), CancellationToken.None);
        await store.RecordStartAsync(Now.AddDays(-2), CancellationToken.None);
        await store.RecordStartAsync(Now.AddDays(-2), CancellationToken.None);
        await store.RecordStartAsync(Now, CancellationToken.None);

        Assert.Equal(3, await store.CountStartsSinceAsync(Now.AddDays(-7), CancellationToken.None));
    }

    [Fact]
    public async Task Pruning_drops_what_is_older_than_the_cutoff_and_keeps_the_rest()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;
        await store.RecordStartAsync(Now.AddDays(-30), CancellationToken.None);
        await store.RecordStartAsync(Now.AddDays(-1), CancellationToken.None);
        await store.RecordDriveSpaceAsync("D:", new DateOnly(2026, 8, 30), 1, 2, CancellationToken.None);
        await store.RecordDriveSpaceAsync("D:", new DateOnly(2026, 9, 30), 1, 2, CancellationToken.None);

        await store.PruneAsync(Now.AddDays(-14), CancellationToken.None);

        Assert.Equal(1, await store.CountStartsSinceAsync(Now.AddDays(-60), CancellationToken.None));
        Assert.Equal(new DateOnly(2026, 9, 30), Assert.Single(await store.ListDriveSpaceAsync(new DateOnly(2026, 1, 1), CancellationToken.None)).Day);
    }

    [Fact]
    public async Task The_jobs_of_the_day_count_finished_and_running_and_failures_and_the_oldest_waiting_job()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;
        var midnight = Now.Date;
        var since = new DateTimeOffset(midnight, TimeSpan.Zero);

        await InsertJobAsync(storage, "done-today", "completed", completed: since.AddHours(3), scheduled: since.AddHours(2));
        await InsertJobAsync(storage, "done-yesterday", "completed", completed: since.AddHours(-3), scheduled: since.AddHours(-4));
        await InsertJobAsync(storage, "gave-up-today", "dead-letter", completed: since.AddHours(5), scheduled: since.AddHours(4));
        await InsertJobAsync(storage, "retrying", "failed", completed: null, scheduled: Now.AddMinutes(5));
        await InsertJobAsync(storage, "running", "running", completed: null, scheduled: Now.AddMinutes(-1));
        await InsertJobAsync(storage, "waiting-old", "queued", completed: null, scheduled: Now.AddMinutes(-30));
        await InsertJobAsync(storage, "waiting-new", "queued", completed: null, scheduled: Now.AddMinutes(-2));
        await InsertJobAsync(storage, "not-yet-due", "queued", completed: null, scheduled: Now.AddHours(1));

        var stats = await store.GetJobDayStatsAsync(since, Now, CancellationToken.None);

        Assert.Equal(3, stats.JobsToday);          // done today + gave up today + running
        Assert.Equal(2, stats.JobsFailedToday);    // gave up today + retrying
        Assert.Equal(1800, stats.OldestWaitingSeconds);
    }

    [Fact]
    public async Task With_nothing_waiting_there_is_no_oldest_waiting_time()
    {
        var (storage, store) = await CreateAsync();
        using var _ = storage;

        var stats = await store.GetJobDayStatsAsync(Now.AddHours(-12), Now, CancellationToken.None);

        Assert.Equal(0, stats.JobsToday);
        Assert.Equal(0, stats.JobsFailedToday);
        Assert.Null(stats.OldestWaitingSeconds);
    }

    private static async Task InsertJobAsync(TestStorage storage, string id, string status, DateTimeOffset? completed, DateTimeOffset scheduled)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO job_queue (id, job_type, source, status, attempts, created_utc, scheduled_utc, completed_utc)
            VALUES (@id, 'test', 'test', @status, 0, @created, @scheduled, @completed);
            """;
        Add(command, "@id", id);
        Add(command, "@status", status);
        Add(command, "@created", scheduled.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@scheduled", scheduled.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@completed", completed?.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
