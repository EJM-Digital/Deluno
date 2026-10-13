using System.Data.Common;
using System.Text.Json.Nodes;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;

using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Migrations;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>#1083: databases that grew to gigabytes because every search kept every release and every check cycle rewrote the same Activity line.</summary>
public sealed class SearchHistoryAndActivityRepeatTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));

    // ---- the shape a row keeps ------------------------------------------------------------

    [Fact]
    public void A_trimmed_search_keeps_what_the_explanation_screen_shows()
    {
        var trimmed = JsonNode.Parse(SearchHistoryDetails.Trim(BigPlan(127))!)!.AsObject();

        // The chosen release, the outcome text, and the reason the search ended as it did.
        Assert.Equal("Best feed candidate is Show S01E01 1080p", trimmed["Summary"]!.GetValue<string>());
        Assert.Equal("ok", trimmed["Reason"]!.GetValue<string>());
        Assert.Equal("Release 0", trimmed["BestCandidate"]!["ReleaseName"]!.GetValue<string>());

        // The six releases the drawer lists, each with its verdict and why.
        var kept = trimmed["Candidates"]!.AsArray();
        Assert.Equal(SearchHistoryDetails.KeptCandidates, kept.Count);
        var first = kept[0]!.AsObject();
        Assert.Equal("Release 0", first["ReleaseName"]!.GetValue<string>());
        Assert.Equal("WEB 1080p", first["Quality"]!.GetValue<string>());
        Assert.Equal("preferred", first["DecisionStatus"]!.GetValue<string>());
        Assert.Equal(2, first["DecisionReasons"]!.AsArray().Count);
        Assert.NotNull(first["PreferenceEvaluation"]);

        // How many were considered, and how the rest fared.
        Assert.Equal(127, trimmed["CandidateCount"]!.GetValue<int>());
        Assert.Equal(56, trimmed["CandidateStatusCounts"]!["rejected"]!.GetValue<int>());

        // What no screen shows is gone: the download link (it can carry an indexer key) and the scoring tree.
        Assert.Null(first["DownloadUrl"]);
        Assert.Null(first["MatchedCustomFormats"]);
        Assert.Null(first["PreferenceEvaluation"]!["Families"]);
        Assert.True(SearchHistoryDetails.Trim(BigPlan(127))!.Length < 12_000);
    }

    [Fact]
    public void A_plan_nested_under_searchPlan_is_trimmed_in_place()
    {
        var wrapped = new JsonObject
        {
            ["EpisodeId"] = "e1",
            ["searchPlan"] = JsonNode.Parse(BigPlan(40))
        }.ToJsonString();

        var trimmed = JsonNode.Parse(SearchHistoryDetails.Trim(wrapped)!)!.AsObject();

        Assert.Equal("e1", trimmed["EpisodeId"]!.GetValue<string>());
        Assert.Equal(40, trimmed["searchPlan"]!["CandidateCount"]!.GetValue<int>());
        Assert.Equal(SearchHistoryDetails.KeptCandidates, trimmed["searchPlan"]!["Candidates"]!.AsArray().Count);
    }

    [Fact]
    public void Trimming_is_idempotent_and_leaves_other_details_alone()
    {
        var once = SearchHistoryDetails.Trim(BigPlan(50))!;
        Assert.Equal(once, SearchHistoryDetails.Trim(once));

        Assert.Equal("{\"queryTitle\":\"x\"}", SearchHistoryDetails.Trim("{\"queryTitle\":\"x\"}"));
        Assert.Equal("not json", SearchHistoryDetails.Trim("not json"));
        Assert.Null(SearchHistoryDetails.Trim(null));
    }

    // ---- the migrations -----------------------------------------------------------------

    [Fact]
    public async Task The_series_migration_trims_existing_rows_compacts_the_file_and_is_idempotent()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        var before = SeriesDatabaseMigrations.All.Where(m => m.Version < 54).ToArray();
        await migrator.ApplyAsync(DelunoDatabaseNames.Series, before, CancellationToken.None);

        var big = BigPlan(127);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series))
        {
            await Execute(connection, "PRAGMA foreign_keys = OFF;");
            for (var i = 0; i < 30; i++)
            {
                await Execute(connection, "INSERT INTO series_search_history (id, series_id, library_id, trigger_kind, outcome, details_json, created_utc) VALUES (@id, 's', 'l', 'automatic', 'held', @d, '2026-09-20T00:00:00Z');",
                    ("@id", $"h{i}"), ("@d", big));
            }

            await Execute(connection, "INSERT INTO series_search_history (id, series_id, library_id, trigger_kind, outcome, details_json, created_utc) VALUES ('none', 's', 'l', 'manual', 'checked', NULL, '2026-09-20T00:00:00Z');");
            await Execute(connection, "PRAGMA foreign_keys = ON;");
        }

        var path = Path.Combine(storage.DataRoot, "series.db");
        storage.Factory.ClearPools();
        var sizeBefore = new FileInfo(path).Length;
        Assert.True(sizeBefore > 500_000);

        await migrator.ApplyAsync(DelunoDatabaseNames.Series, SeriesDatabaseMigrations.All, CancellationToken.None);
        await migrator.ApplyAsync(DelunoDatabaseNames.Series, SeriesDatabaseMigrations.All, CancellationToken.None);
        storage.Factory.ClearPools();

        Assert.True(new FileInfo(path).Length < sizeBefore / 3, $"the file went from {sizeBefore} to {new FileInfo(path).Length}");
        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        Assert.Equal(31, await Scalar<long>(after, "SELECT COUNT(*) FROM series_search_history;"));
        Assert.Equal(0, await Scalar<long>(after, "PRAGMA freelist_count;"));
        Assert.True(await Scalar<long>(after, "SELECT MAX(length(details_json)) FROM series_search_history;") < 12_000);
        Assert.Equal(127, await Scalar<long>(after, "SELECT json_extract(details_json, '$.CandidateCount') FROM series_search_history WHERE id = 'h3';"));
        Assert.Equal(1, await Scalar<long>(after, "SELECT COUNT(*) FROM schema_migrations WHERE version = 54;"));
    }

    [Fact]
    public async Task The_movie_migration_trims_existing_rows()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        await migrator.ApplyAsync(DelunoDatabaseNames.Movies, MoviesDatabaseMigrations.All.Where(m => m.Version < 51).ToArray(), CancellationToken.None);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            await Execute(connection, "PRAGMA foreign_keys = OFF;");
            await Execute(connection, "INSERT INTO movie_search_history (id, movie_id, library_id, trigger_kind, outcome, details_json, created_utc) VALUES ('h1', 'm', 'l', 'automatic', 'matched', @d, '2026-09-20T00:00:00Z');", ("@d", BigPlan(90)));
            await Execute(connection, "PRAGMA foreign_keys = ON;");
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Movies, MoviesDatabaseMigrations.All, CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        Assert.Equal(90, await Scalar<long>(after, "SELECT json_extract(details_json, '$.CandidateCount') FROM movie_search_history WHERE id = 'h1';"));
    }

    [Fact]
    public async Task The_jobs_migration_collapses_repeated_activity_to_one_row_per_state_and_is_idempotent()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, JobsDatabaseMigrations.All.Where(m => m.Version < 41).ToArray(), CancellationToken.None);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            for (var cycle = 0; cycle < 50; cycle++)
            {
                await Insert(connection, $"a{cycle}", "download.vanished.recovered", "Found its files", "movie", "m1", cycle);
                await Insert(connection, $"b{cycle}", "download.vanished.recovered", "Found its files", "movie", "m2", cycle);
                await Insert(connection, $"c{cycle}", "processing.waiting", cycle < 30 ? "Waiting for output" : "Still waiting for output", "download", "h1", cycle);
                await Insert(connection, $"d{cycle}", "job.started", "Started", "job", "j1", cycle);
            }

            await Execute(connection, "INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc) VALUES ('x1', 'download.dispatch.recorded', 'Sent', @d, 'movie', 'm1', '2026-09-27T00:00:00Z');",
                ("@d", new JsonObject { ["searchPlan"] = JsonNode.Parse(BigPlan(100)), ["grabResult"] = new JsonObject { ["Status"] = "succeeded" } }.ToJsonString()));
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, JobsDatabaseMigrations.All, CancellationToken.None);
        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, JobsDatabaseMigrations.All, CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(1, await Scalar<long>(after, "SELECT COUNT(*) FROM activity_events WHERE category = 'download.vanished.recovered' AND related_entity_id = 'm1';"));
        Assert.Equal(1, await Scalar<long>(after, "SELECT COUNT(*) FROM activity_events WHERE category = 'download.vanished.recovered' AND related_entity_id = 'm2';"));
        // Two distinct states, so two rows, each the earliest of its run.
        Assert.Equal(2, await Scalar<long>(after, "SELECT COUNT(*) FROM activity_events WHERE category = 'processing.waiting';"));
        Assert.Equal("c0", await Scalar<string>(after, "SELECT id FROM activity_events WHERE category = 'processing.waiting' AND message = 'Waiting for output';"));
        Assert.Equal("c30", await Scalar<string>(after, "SELECT id FROM activity_events WHERE category = 'processing.waiting' AND message = 'Still waiting for output';"));
        // The candidate list copied into a "sent" line is cut to what the screens show; the grab result stays.
        Assert.Equal(100, await Scalar<long>(after, "SELECT json_extract(details_json, '$.searchPlan.CandidateCount') FROM activity_events WHERE id = 'x1';"));
        Assert.Equal("succeeded", await Scalar<string>(after, "SELECT json_extract(details_json, '$.grabResult.Status') FROM activity_events WHERE id = 'x1';"));
        // Everything else is untouched.
        Assert.Equal(50, await Scalar<long>(after, "SELECT COUNT(*) FROM activity_events WHERE category = 'job.started';"));
    }

    // ---- once per state ------------------------------------------------------------------

    [Fact]
    public async Task A_condition_that_holds_for_many_cycles_writes_one_activity_row_and_again_when_it_changes()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);

        for (var cycle = 0; cycle < 25; cycle++)
        {
            await store.RecordActivityOnChangeAsync("processing.waiting", "Waiting for a cleaned output.", null, null, "download", "h1", CancellationToken.None);
        }

        Assert.Single(await store.ListActivityAsync(100, "download", "h1", CancellationToken.None));

        // Another item's identical line is its own condition.
        await store.RecordActivityOnChangeAsync("processing.waiting", "Waiting for a cleaned output.", null, null, "download", "h2", CancellationToken.None);
        Assert.Single(await store.ListActivityAsync(100, "download", "h2", CancellationToken.None));

        // What it says changes: a new row. Then it holds again: still no more.
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await store.RecordActivityOnChangeAsync("processing.waiting", "The processor is slow to answer.", null, null, "download", "h1", CancellationToken.None);
        }

        Assert.Equal(2, (await store.ListActivityAsync(100, "download", "h1", CancellationToken.None)).Count);
    }

    /// <summary>
    /// Found in a full run (#1260): the test above failed once and passed alone. Activity rows written in the same instant
    /// (a fixed test clock; in the product, two lines inside one tick) were ordered by their random ids, so "the latest line"
    /// could be the older one, and the repeat check wrote another row. The latest line is the one written last.
    /// </summary>
    [Fact]
    public async Task The_latest_line_is_the_one_written_last_even_when_rows_share_an_instant_and_an_older_row_has_the_higher_id()
    {
        using var storage = TestStorage.Create();
        var store = await CreateStoreAsync(storage);
        var at = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        ActivityEventItem Row(string id, string message) => new(id, "processing.waiting", message, null, null, "download", "h1", at);
        // Written in this order, in the same instant; the first has the higher id.
        await store.RecordActivityBatchAsync([Row("ffffffffffffffffffffffffffffffff", "Waiting for a cleaned output.")], CancellationToken.None);
        await store.RecordActivityBatchAsync([Row("00000000000000000000000000000001", "The processor is slow to answer.")], CancellationToken.None);

        var again = await store.RecordActivityOnChangeAsync("processing.waiting", "The processor is slow to answer.", null, null, "download", "h1", CancellationToken.None);

        Assert.Null(again);
        Assert.Equal(2, (await store.ListActivityAsync(100, "download", "h1", CancellationToken.None)).Count);
    }

    // ---- the one-time compaction ---------------------------------------------------------

    [Fact]
    public async Task A_migration_that_asks_for_compaction_compacts_once_and_never_again()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        var fill = new FillMigration();
        var shrink = new ShrinkMigration();
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [fill], CancellationToken.None);
        storage.Factory.ClearPools();
        var path = Path.Combine(storage.DataRoot, "platform.db");
        var full = new FileInfo(path).Length;
        Assert.True(full > 1_000_000);

        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [fill, shrink], CancellationToken.None);
        storage.Factory.ClearPools();
        var compacted = new FileInfo(path).Length;
        Assert.True(compacted < full / 4, $"{full} -> {compacted}");

        // Later data leaves free pages behind; a start-up that finds the migration recorded does not vacuum them away.
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            await Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 30) INSERT INTO fat (id, body) SELECT i, randomblob(50000) FROM n;");
            await Execute(connection, "DELETE FROM fat;");
            Assert.True(await Scalar<long>(connection, "PRAGMA freelist_count;") > 0);
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [fill, shrink], CancellationToken.None);
        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        Assert.True(await Scalar<long>(after, "PRAGMA freelist_count;") > 0);
    }

    private sealed class FillMigration : SqliteSqlMigration
    {
        public override int Version => 1;
        public override string Name => "fill";
        protected override string Sql =>
            """
            CREATE TABLE fat (id INTEGER PRIMARY KEY, body BLOB);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 60)
            INSERT INTO fat (id, body) SELECT i, randomblob(50000) FROM n;
            """;
    }

    private sealed class ShrinkMigration : SqliteSqlMigration
    {
        public override int Version => 2;
        public override string Name => "shrink";
        public override bool CompactsDatabaseAfterApply => true;
        protected override string Sql => "DELETE FROM fat;";
    }

    // ---- helpers -------------------------------------------------------------------------

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteJobStore(storage.Factory, Clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }

    private static string BigPlan(int candidates)
    {
        var items = new JsonArray();
        for (var i = 0; i < candidates; i++)
        {
            items.Add(Candidate(i, i < 16 ? "preferred" : i % 2 == 0 ? "rejected" : "eligible"));
        }

        return new JsonObject
        {
            ["BestCandidate"] = Candidate(0, "preferred"),
            ["Candidates"] = items,
            ["Summary"] = "Best feed candidate is Show S01E01 1080p",
            ["Reason"] = "ok",
            ["CandidatesTruncatedByIndexer"] = false,
            ["Failures"] = new JsonArray()
        }.ToJsonString();
    }

    private static JsonObject Candidate(int i, string status)
        => new()
        {
            ["ReleaseName"] = $"Release {i}",
            ["IndexerId"] = "ix1",
            ["IndexerName"] = "Indexer",
            ["Quality"] = "WEB 1080p",
            ["Score"] = 100 - i,
            ["MeetsCutoff"] = true,
            ["Summary"] = "Quality meets cutoff",
            ["DownloadUrl"] = "https://indexer.example/download?apikey=secret&id=" + new string('x', 300),
            ["SizeBytes"] = 3_000_000_000L,
            ["Seeders"] = 9,
            ["DecisionStatus"] = status,
            ["DecisionReasons"] = new JsonArray("Meets cutoff.", "9 seeders reported."),
            ["RiskFlags"] = new JsonArray(),
            ["MatchedCustomFormats"] = new JsonArray(new JsonObject { ["Name"] = new string('c', 300) }),
            ["PreferenceEvaluation"] = new JsonObject
            {
                ["PlanId"] = "plan",
                ["PlanVersion"] = new string('v', 100),
                ["PlanHash"] = new string('h', 64),
                ["Status"] = 3,
                ["HardGatesPassed"] = true,
                ["TargetsMet"] = true,
                ["Families"] = new JsonArray(Enumerable.Range(0, 11).Select(n => (JsonNode)new JsonObject { ["FamilyId"] = $"f{n}", ["Explanation"] = new string('e', 200) }).ToArray()),
                ["Reasons"] = new JsonArray("ok")
            }
        };

    private static async Task Insert(DbConnection connection, string id, string category, string message, string type, string entity, int cycle)
        => await Execute(connection,
            "INSERT INTO activity_events (id, category, message, related_entity_type, related_entity_id, created_utc) VALUES (@id, @c, @m, @t, @e, @u);",
            ("@id", id), ("@c", category), ("@m", message), ("@t", type), ("@e", entity), ("@u", DateTimeOffset.Parse("2026-09-27T00:00:00Z").AddMinutes(cycle).ToString("O")));

    private static async Task Execute(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
