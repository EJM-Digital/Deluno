using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Recovery.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The dispatch cleanup pass on the real jobs database: which failed dispatches it archives and which it leaves,
/// the batch limit, and the timeline line it writes for each one archived.
/// </summary>
public sealed class TheDispatchCleanupPassOnTheRealStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T08:00:00Z");

    private static async Task InsertAsync(TestStorage storage, string id, string status, string? grabFailureCode, string? importFailureCode, int ageDays)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        await using var command = connection.CreateCommand();
        var attempted = Now.AddDays(-ageDays).ToString("O");
        command.CommandText =
            "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc, grab_attempted_utc, grab_failure_code, import_failure_code) " +
            $"VALUES ('{id}', 'lib', 'movies', 'movie', 'm-{id}', 'Release.{id}', 'Indexer', 'c', 'C', '{status}', '{attempted}', '{attempted}', {(grabFailureCode is null ? "NULL" : $"'{grabFailureCode}'")}, {(importFailureCode is null ? "NULL" : $"'{importFailureCode}'")});";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(TestStorage Storage, SqliteDownloadDispatchesRepository Dispatches, DispatchCleanupService Service)> SceneAsync()
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        return (storage, dispatches, new DispatchCleanupService(dispatches, clock, NullLogger<DispatchCleanupService>.Instance));
    }

    [Fact]
    public async Task A_failed_dispatch_older_than_seven_days_is_archived_with_a_timeline_line_unless_it_is_waiting_on_something()
    {
        var (storage, dispatches, service) = await SceneAsync();
        using var owned = storage;
        await InsertAsync(storage, "old-timeout", "failed", "timeout", null, ageDays: 9);
        await InsertAsync(storage, "old-no-code", "failed", null, null, ageDays: 8);
        await InsertAsync(storage, "old-retries", "failed", null, "max-retries-exceeded", ageDays: 30);
        await InsertAsync(storage, "old-notfound", "failed", "notFound", null, ageDays: 9);
        await InsertAsync(storage, "old-paused", "failed", "paused", null, ageDays: 9);
        await InsertAsync(storage, "old-planned", "failed", "planned", null, ageDays: 9);
        await InsertAsync(storage, "old-circuit", "failed", "circuitOpen", null, ageDays: 9);
        await InsertAsync(storage, "recent-failed", "failed", "timeout", null, ageDays: 3);
        await InsertAsync(storage, "old-sent", "sent", null, null, ageDays: 20);

        var result = await service.RunCleanupPassAsync(CancellationToken.None);

        Assert.Equal(3, result.ArchivedCount);
        Assert.Equal(4, result.SkippedCount);
        Assert.Equal("Archived 3 stale dispatch(es); skipped 4.", result.Summary);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        async Task<string> StatusAsync(string id)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT status FROM download_dispatches WHERE id = '{id}';";
            return (string)(await command.ExecuteScalarAsync())!;
        }

        foreach (var archived in new[] { "old-timeout", "old-no-code", "old-retries" })
        {
            Assert.Equal("archived", await StatusAsync(archived));
            var line = Assert.Single(await dispatches.GetDispatchTimelineAsync(archived, CancellationToken.None), e => e.EventType == "auto-archived");
            Assert.Contains("Exceeded retention period", line.DetailsJson);
        }

        foreach (var kept in new[] { "old-notfound", "old-paused", "old-planned", "old-circuit", "recent-failed" })
        {
            Assert.Equal("failed", await StatusAsync(kept));
            Assert.DoesNotContain(await dispatches.GetDispatchTimelineAsync(kept, CancellationToken.None), e => e.EventType == "auto-archived");
        }

        Assert.Equal("sent", await StatusAsync("old-sent"));
    }

    [Fact]
    public async Task One_pass_archives_at_most_fifty_and_the_next_pass_takes_the_rest_and_an_empty_pass_says_so()
    {
        var (storage, _, service) = await SceneAsync();
        using var owned = storage;
        Assert.Equal("No stale dispatches found.", (await service.RunCleanupPassAsync(CancellationToken.None)).Summary);

        for (var index = 0; index < 55; index++)
        {
            await InsertAsync(storage, "stale-" + index, "failed", "timeout", null, ageDays: 10);
        }

        var first = await service.RunCleanupPassAsync(CancellationToken.None);
        Assert.Equal(50, first.ArchivedCount);

        var second = await service.RunCleanupPassAsync(CancellationToken.None);
        Assert.Equal(5, second.ArchivedCount);
        Assert.Equal(0, (await service.RunCleanupPassAsync(CancellationToken.None)).ArchivedCount);
    }
}
