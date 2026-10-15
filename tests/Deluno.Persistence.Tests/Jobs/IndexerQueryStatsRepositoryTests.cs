using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class IndexerQueryStatsRepositoryTests
{
    [Fact]
    public async Task Batch_aggregates_query_kinds_failures_and_grabs_and_prunes_old_rows()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-30T12:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteIndexerQueryStatsRepository(storage.Factory);
        await repository.RecordBatchAsync(
            [
                Entry("indexer-a", "Alpha", "search", "matched", 120, 2, now.AddDays(-5)),
                Entry(
                    "indexer-a",
                    "Alpha",
                    "search",
                    "failed",
                    30,
                    0,
                    now.AddDays(-4),
                    IntegrationFailureFactory.FromLegacy(
                        "indexer",
                        "indexer-a",
                        "Alpha",
                        "search",
                        "ratelimit",
                        "The indexer asked Deluno to slow down.",
                        code: "rate-limited")),
                Entry("indexer-a", "Alpha", "auth", "matched", 20, 0, now.AddDays(-3)),
                Entry("indexer-b", "Beta", "rss", "no_results", 90, 0, now.AddDays(-2)),
                Entry("indexer-a", "Alpha", "search", "matched", 999, 1, now.AddDays(-40))
            ],
            CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT failure_json FROM indexer_query_events WHERE indexer_id = 'indexer-a' AND outcome = 'failed' LIMIT 1;";
            var failureJson = (string?)await command.ExecuteScalarAsync();
            Assert.False(string.IsNullOrWhiteSpace(failureJson));
            using var document = JsonDocument.Parse(failureJson!);
            Assert.Equal("rate-limited", document.RootElement.GetProperty("Code").GetString());
            Assert.Equal("RateLimit", document.RootElement.GetProperty("Kind").GetString());
        }

        await InsertDispatchAsync(storage.Factory, "dispatch-a", "Alpha", "succeeded", now.AddDays(-5));
        await InsertDispatchAsync(storage.Factory, "dispatch-b", "Alpha", "failed", now.AddDays(-4));
        await InsertDispatchAsync(storage.Factory, "dispatch-c", "Beta", "succeeded", now.AddDays(-2));

        var snapshot = await repository.GetScoreboardAsync(
            now.AddDays(-30),
            now.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(4, snapshot.TotalQueries);
        Assert.Equal(3, snapshot.TotalGrabs);
        Assert.Equal(2, snapshot.SuccessfulGrabs);

        var alpha = Assert.Single(snapshot.QueryStats, item => item.IndexerId == "indexer-a");
        Assert.Equal(3, alpha.TotalQueries);
        Assert.Equal(2, alpha.SearchQueries);
        Assert.Equal(0, alpha.RssQueries);
        Assert.Equal(1, alpha.AuthQueries);
        Assert.Equal(1, alpha.FailedQueries);
        Assert.Equal(56.666, alpha.AverageResponseMilliseconds, 2);
        Assert.Equal(2, alpha.CandidatesReturned);
        Assert.Equal(now.AddDays(-3), alpha.LastQueryUtc);

        var beta = Assert.Single(snapshot.QueryStats, item => item.IndexerId == "indexer-b");
        Assert.Equal(1, beta.RssQueries);
        Assert.Equal(0, beta.FailedQueries);

        var alphaGrabs = Assert.Single(snapshot.GrabStats, item => item.IndexerName == "Alpha");
        Assert.Equal(2, alphaGrabs.TotalGrabs);
        Assert.Equal(1, alphaGrabs.SuccessfulGrabs);

        Assert.Equal(1, await repository.PruneAsync(now.AddDays(-30), CancellationToken.None));
        var afterPrune = await repository.GetScoreboardAsync(
            now.AddDays(-60),
            now.AddMinutes(1),
            CancellationToken.None);
        Assert.Equal(4, afterPrune.TotalQueries);
    }

    /// <summary>#1330: every reason Deluno itself holds a call back is kept apart; none of them is a query or a failure on the scoreboard.</summary>
    [Theory]
    [InlineData("throttled")]
    [InlineData("query_limit_reached")]
    [InlineData("instance_query_limit_reached")]
    [InlineData("circuit_open")]
    public async Task A_call_Deluno_held_back_is_counted_on_its_own_and_never_as_a_query_or_a_failure(string reason)
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-30T12:00:00Z");
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(now)),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteIndexerQueryStatsRepository(storage.Factory);

        await repository.RecordBatchAsync(
            [
                Entry("indexer-a", "Alpha", "search", "matched", 100, 3, now.AddHours(-2)),
                Entry("indexer-a", "Alpha", "search", reason, 0, 0, now.AddHours(-1)),
                Entry("indexer-a", "Alpha", "rss", reason, 0, 0, now.AddMinutes(-30)),
                Entry("indexer-b", "Beta", "search", reason, 0, 0, now.AddMinutes(-10))
            ],
            CancellationToken.None);

        var snapshot = await repository.GetScoreboardAsync(now.AddDays(-30), now, CancellationToken.None);

        var alpha = Assert.Single(snapshot.QueryStats, item => item.IndexerId == "indexer-a");
        Assert.Equal(1, alpha.TotalQueries);
        Assert.Equal(1, alpha.SearchQueries);
        Assert.Equal(0, alpha.RssQueries);
        Assert.Equal(0, alpha.FailedQueries);
        Assert.Equal(2, alpha.HeldBackQueries);
        Assert.Equal(100, alpha.AverageResponseMilliseconds);
        Assert.Equal(now.AddHours(-2), alpha.LastQueryUtc);
        var beta = Assert.Single(snapshot.QueryStats, item => item.IndexerId == "indexer-b");
        Assert.Equal(0, beta.TotalQueries);
        Assert.Equal(0, beta.FailedQueries);
        Assert.Equal(1, beta.HeldBackQueries);
        Assert.Null(beta.LastQueryUtc);
        Assert.Equal(1, snapshot.TotalQueries);
    }

    private static IndexerQueryLogEntry Entry(
        string id,
        string name,
        string kind,
        string outcome,
        int elapsed,
        int candidates,
        DateTimeOffset createdUtc,
        IntegrationFailure? failure = null)
        => new(
            IndexerId: id,
            IndexerName: name,
            QueryText: "A title",
            Categories: "2000,5000",
            MediaType: "movies",
            QueryKind: kind,
            Outcome: outcome,
            ElapsedMilliseconds: elapsed,
            CandidateCount: candidates,
            CreatedUtc: createdUtc,
            Failure: failure);

    private static async Task InsertDispatchAsync(
        IDelunoDatabaseConnectionFactory factory,
        string id,
        string indexerName,
        string grabStatus,
        DateTimeOffset createdUtc)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                notes_json, created_utc, grab_status
            ) VALUES (
                @id, 'library', 'movies', 'movie', @id, 'Release', @indexerName,
                'client', 'Client', 'sent', NULL, @createdUtc, @grabStatus
            );
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@indexerName", indexerName);
        AddParameter(command, "@createdUtc", createdUtc.ToUniversalTime().ToString("O"));
        AddParameter(command, "@grabStatus", grabStatus);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
