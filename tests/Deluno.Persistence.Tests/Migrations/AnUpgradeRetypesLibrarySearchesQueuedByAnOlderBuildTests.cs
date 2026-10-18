using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>
/// `library.search` became `library.search.movies` and `library.search.tv`. A search an older build left queued, failed or
/// running is re-typed on upgrade by what its own payload says, so no lane is left without it, and a finished one keeps the
/// name it ran under. Run against a real jobs database file holding rows as the older build wrote them.
/// </summary>
public sealed class AnUpgradeRetypesLibrarySearchesQueuedByAnOlderBuildTests
{
    [Fact]
    public async Task A_search_queued_failed_or_running_under_the_old_name_takes_the_type_of_its_media_and_a_finished_one_keeps_its_name()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T06:00:00Z"));
        await new JobsSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        await InsertAsync(storage, "film-queued", "queued", """{"mediaType":"movies"}""");
        await InsertAsync(storage, "show-failed", "failed", """{"mediaType":"tv"}""");
        await InsertAsync(storage, "show-running", "running", """{"mediaType":"series"}""");
        await InsertAsync(storage, "show-spelled-out", "queued", """{"mediaType":"Television"}""");
        await InsertAsync(storage, "no-media-type", "queued", """{"libraryId":"movies-main"}""");
        await InsertAsync(storage, "no-payload", "queued", null);
        await InsertAsync(storage, "show-finished", "completed", """{"mediaType":"tv"}""");

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await new V0017LibrarySearchPerMediaTypeJobTypes().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal("library.search.movies", await TypeOfAsync(storage, "film-queued"));
        Assert.Equal("library.search.tv", await TypeOfAsync(storage, "show-failed"));
        Assert.Equal("library.search.tv", await TypeOfAsync(storage, "show-running"));
        Assert.Equal("library.search.tv", await TypeOfAsync(storage, "show-spelled-out"));
        Assert.Equal("library.search.movies", await TypeOfAsync(storage, "no-media-type"));
        Assert.Equal("library.search.movies", await TypeOfAsync(storage, "no-payload"));
        Assert.Equal("library.search", await TypeOfAsync(storage, "show-finished"));

        // Every type it writes is leased by a lane, so the re-typed work is not orphaned.
        var routed = JobLanes.All.SelectMany(lane => lane.JobTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("library.search.movies", routed);
        Assert.Contains("library.search.tv", routed);
    }

    private static async Task InsertAsync(TestStorage storage, string id, string status, string? payload)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        await using var command = connection.CreateCommand();
        var payloadSql = payload is null ? "NULL" : $"'{payload}'";
        command.CommandText =
            $"""
            INSERT INTO job_queue (id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc, related_entity_type, related_entity_id)
            VALUES ('{id}', 'library.search', 'test', '{status}', {payloadSql}, 0, '2026-04-01T00:00:00Z', '2026-04-01T00:00:00Z', 'library', 'movies-main');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> TypeOfAsync(TestStorage storage, string id)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT job_type FROM job_queue WHERE id = '{id}';";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
