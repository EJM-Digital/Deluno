using System.Net;
using System.Net.Http.Json;
using Deluno.Api.SystemInfo;
using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1098: the System screen's Log card, Backups and databases card and Scheduled tasks card read real data.
/// </summary>
public sealed class SystemLogAndDatabasesEndpointTests
{
    [Fact]
    public async Task The_log_returns_todays_warnings_and_errors_newest_first_and_filters_by_level()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var buffer = app.Services.GetRequiredService<DelunoLogBuffer>();
        buffer.Add(LogLevel.Warning, "Deluno.Worker.HealthCheckHostedService", "The indexer answered slowly.");
        buffer.Add(LogLevel.Error, "Deluno.Api.DelunoBackupService", "The backup folder is not writable.");
        buffer.Add(LogLevel.Debug, "Deluno.Anything", "Not kept.");
        buffer.Add(LogLevel.Information, "Deluno.Api.Routine", "Not notable, not kept.");

        var all = await app.Client.GetFromJsonAsync<LogBody>("/api/system/log?level=all&take=50");
        var errors = await app.Client.GetFromJsonAsync<LogBody>("/api/system/log?level=error");
        var warnings = await app.Client.GetFromJsonAsync<LogBody>("/api/system/log?level=warning");

        Assert.NotNull(all);
        Assert.Contains(all!.Entries, entry => entry.Message == "The indexer answered slowly." && entry.Level == "warning" && entry.Source == "HealthCheck");
        Assert.DoesNotContain(all.Entries, entry => entry.Message == "Not kept." || entry.Message.StartsWith("Not notable", StringComparison.Ordinal));
        Assert.True(all.Entries.Select(entry => entry.TimeUtc).SequenceEqual(all.Entries.Select(entry => entry.TimeUtc).OrderByDescending(time => time)));
        Assert.All(errors!.Entries, entry => Assert.Equal("error", entry.Level));
        Assert.Contains(errors.Entries, entry => entry.Message == "The backup folder is not writable.");
        Assert.All(warnings!.Entries, entry => Assert.Equal("warning", entry.Level));
        Assert.True(all.ErrorsToday >= 1);
        Assert.True(all.WarningsToday >= 1);
    }

    [Fact]
    public async Task The_log_refuses_a_level_it_does_not_know()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/system/log?level=loud");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void An_information_line_is_kept_only_when_it_is_about_something_a_person_cares_about()
    {
        Assert.True(DelunoLogBuffer.IsNotable(LogLevel.Information, "Deluno.Api.DelunoBackupService"));
        Assert.True(DelunoLogBuffer.IsNotable(LogLevel.Warning, "Deluno.Anything"));
        Assert.False(DelunoLogBuffer.IsNotable(LogLevel.Information, "Deluno.Worker.WorkPlanner"));
        Assert.False(DelunoLogBuffer.IsNotable(LogLevel.Debug, "Deluno.Api.DelunoBackupService"));
    }

    [Fact]
    public void The_buffer_keeps_one_line_and_at_most_its_capacity_and_announces_each_entry()
    {
        var buffer = new DelunoLogBuffer();
        var announced = new List<LogEntryView>();
        buffer.Added += announced.Add;

        buffer.Add(LogLevel.Warning, "Deluno.X", "First line\nsecond line with a stack");
        for (var i = 0; i < DelunoLogBuffer.Capacity + 20; i++)
        {
            buffer.Add(LogLevel.Error, "Deluno.X", $"error {i}");
        }

        Assert.Equal(DelunoLogBuffer.Capacity + 21, announced.Count);
        Assert.Equal("First line", announced[0].Message);
        Assert.Equal(DelunoLogBuffer.Capacity, buffer.Recent("all", 10_000).Count);
    }

    [Fact]
    public async Task The_log_card_is_filled_after_a_restart_from_todays_log_file_without_secrets()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logDirectory = Path.Combine(app.DataRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        var now = DateTimeOffset.UtcNow;
        var line = $"{{\"timestamp\":\"{now.UtcDateTime:O}\",\"level\":\"Error\",\"category\":\"Deluno.Api.Thing\",\"message\":\"The disk is full.\"}}";
        await File.WriteAllTextAsync(Path.Combine(logDirectory, $"deluno-{now:yyyyMMdd}.9.jsonl"), line + "\n");

        var log = await app.Client.GetFromJsonAsync<LogBody>("/api/system/log?level=error");

        Assert.Contains(log!.Entries, entry => entry.Message == "The disk is full." && entry.Source == "Thing");
        Assert.StartsWith("deluno-", log.LatestLogFile);
    }

    [Fact]
    public async Task The_file_log_feeds_the_buffer_with_secrets_already_masked()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var buffer = app.Services.GetRequiredService<DelunoLogBuffer>();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();

        factory.CreateLogger("Deluno.Api.Check").LogWarning("Reached the service with apikey={Key}", "abcd1234secretvalue");

        var entry = buffer.Recent("warning", 20).First(item => item.Source == "Check");
        Assert.DoesNotContain("abcd1234secretvalue", entry.Message);
    }

    [Fact]
    public async Task Databases_lists_each_file_with_its_size_and_when_it_was_last_checked_and_compacted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        DatabaseMaintenanceLog.RecordIntegrity(app.DataRoot, DateTimeOffset.UtcNow.AddHours(-3), true, null);
        DatabaseMaintenanceLog.RecordCompaction(app.DataRoot, DateTimeOffset.UtcNow.AddDays(-2));

        var body = await app.Client.GetFromJsonAsync<DatabasesBody>("/api/system/databases");

        Assert.NotNull(body);
        Assert.NotEmpty(body!.Databases);
        Assert.All(body.Databases, database => Assert.True(database.SizeBytes > 0));
        Assert.Contains(body.Databases, database => database.Name == "Movies");
        Assert.Equal(body.Databases.Sum(database => database.SizeBytes), body.TotalBytes);
        Assert.True(body.IntegrityOk);
        Assert.InRange((DateTimeOffset.UtcNow - body.IntegrityCheckedUtc!.Value).TotalHours, 2.9, 3.1);
        Assert.NotNull(body.CompactedUtc);
    }

    [Fact]
    public async Task The_daily_check_runs_over_every_database_and_records_a_pass()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var service = app.Services.GetRequiredService<DatabaseIntegrityService>();

        await app.Services.GetRequiredService<IJobQueueRepository>().TryClaimScheduledPassAsync(SystemTasks.DatabaseIntegrity, TimeSpan.FromDays(1), CancellationToken.None);

        var problems = await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(problems);
        var maintenance = DatabaseMaintenanceLog.Read(app.DataRoot);
        Assert.True(maintenance.IntegrityOk);
        Assert.NotNull(maintenance.IntegrityCheckedUtc);
        var states = await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None);
        Assert.Equal("completed", states.Single(state => state.ScheduleKey == SystemTasks.DatabaseIntegrity).LastResult);
    }

    [Fact]
    public async Task A_damaged_database_is_named_in_plain_words_in_the_note_and_the_pass_is_recorded_as_failed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var path = factory.GetDatabasePath("movies");
        // A real copy of the database file with a stretch of its second page overwritten: what a bad disk or a half-copied backup does.
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Position = 4096;
            await stream.WriteAsync(Enumerable.Repeat((byte)0xFF, 3000).ToArray());
        }

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.TryClaimScheduledPassAsync(SystemTasks.DatabaseIntegrity, TimeSpan.FromDays(1), CancellationToken.None);
        var problems = await app.Services.GetRequiredService<DatabaseIntegrityService>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(["Movies"], problems);
        var maintenance = DatabaseMaintenanceLog.Read(app.DataRoot);
        Assert.False(maintenance.IntegrityOk);
        Assert.Equal("Problem found in: Movies.", maintenance.IntegrityNote);
        var state = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.DatabaseIntegrity);
        Assert.Equal("failed", state.LastResult);
        Assert.Equal("Problem found in: Movies.", state.LastError);
    }

    [Fact]
    public async Task A_daily_check_that_cannot_read_a_database_is_recorded_failed_with_a_plain_reason_not_as_a_damaged_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var service = new DatabaseIntegrityService(
            new CannotOpenForReading(app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>()),
            jobs,
            app.Services.GetRequiredService<IHealthCheckRepository>(),
            TimeProvider.System,
            NullLogger<DatabaseIntegrityService>.Instance);
        await jobs.TryClaimScheduledPassAsync(SystemTasks.DatabaseIntegrity, TimeSpan.FromDays(1), CancellationToken.None);

        var problems = await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(problems);
        var state = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.DatabaseIntegrity);
        Assert.Equal("failed", state.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(state.LastError));
        Assert.DoesNotContain("InvalidOperationException", state.LastError);
    }

    private sealed class CannotOpenForReading(IDelunoDatabaseConnectionFactory real) : IDelunoDatabaseConnectionFactory
    {
        public string GetDatabasePath(string databaseName) => real.GetDatabasePath(databaseName);

        public ValueTask<System.Data.Common.DbConnection> OpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
            => real.OpenConnectionAsync(databaseName, cancellationToken);

        public ValueTask<System.Data.Common.DbConnection> OpenReadOnlyConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the disk went away");

        public long GetWriteGeneration(string databaseName) => real.GetWriteGeneration(databaseName);
    }

    [Fact]
    public async Task A_failed_task_keeps_its_reason_in_plain_words_and_a_later_success_clears_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        const string key = SystemTasks.HealthChecks;
        await jobs.TryClaimScheduledPassAsync(key, TimeSpan.FromMinutes(15), CancellationToken.None);

        await jobs.RecordScheduledPassOutcomeAsync(key, DateTimeOffset.UtcNow, "failed", 5, null, CancellationToken.None,
            PlainFailure.Describe(new HttpRequestException("x", null, HttpStatusCode.Unauthorized), "checking health"));
        var failed = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);

        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));

        await jobs.RecordScheduledPassOutcomeAsync(key, DateTimeOffset.UtcNow, "completed", 5, null, CancellationToken.None);
        var ok = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);
        Assert.Null(ok.LastError);

        var tasks = await app.Client.GetStringAsync("/api/system/tasks");
        Assert.Contains("\"lastError\"", tasks);
    }

    private sealed record LogBody(List<LogEntryBody> Entries, int ErrorsToday, int WarningsToday, string? LatestLogFile);

    private sealed record LogEntryBody(string Id, DateTimeOffset TimeUtc, string Level, string Message, string? Source);

    private sealed record DatabasesBody(
        List<DatabaseBody> Databases,
        long TotalBytes,
        DateTimeOffset? IntegrityCheckedUtc,
        bool? IntegrityOk,
        string? IntegrityNote,
        DateTimeOffset? CompactedUtc);

    private sealed record DatabaseBody(string Key, string Name, long SizeBytes);
}
