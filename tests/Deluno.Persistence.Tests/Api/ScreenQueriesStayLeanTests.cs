using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Caching;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1081: what keeps a screen's calls small and quick, asserted without a clock
/// where it can be (a query plan has none, so it cannot flake) and against the
/// real routes where it cannot.
/// </summary>
public sealed class ScreenQueriesStayLeanTests
{
    // ------------------------------------------------------------------ query plans

    [Theory]
    [InlineData("SELECT id FROM job_queue ORDER BY created_utc DESC, id DESC LIMIT 81;", "ix_job_queue_created_id")]
    [InlineData("SELECT id FROM job_queue WHERE status IN ('failed', 'dead-letter') ORDER BY created_utc DESC, id DESC LIMIT 501;", "ix_job_queue_status_created")]
    [InlineData("SELECT id FROM download_dispatches WHERE status != 'archived' ORDER BY COALESCE(grab_attempted_utc, created_utc) DESC, id DESC LIMIT 51;", "ix_download_dispatches_sort")]
    [InlineData("SELECT id FROM download_dispatches ORDER BY COALESCE(grab_attempted_utc, created_utc) DESC, id DESC LIMIT 51;", "ix_download_dispatches_sort")]
    [InlineData("SELECT id FROM activity_events WHERE related_entity_type = 'movie' ORDER BY created_utc DESC, id DESC LIMIT 51;", "ix_activity_events_entity_type_created")]
    [InlineData("SELECT id FROM activity_events WHERE related_entity_type = 'movie' AND related_entity_id = 'x' ORDER BY created_utc DESC, id DESC LIMIT 51;", "ix_activity_events_entity_created")]
    [InlineData("SELECT substr(completed_utc, 1, 10) AS day, COUNT(*) FROM job_queue WHERE status = 'completed' AND completed_utc >= '2026-01-01' AND completed_utc < '2026-02-01' GROUP BY day;", "ix_job_queue_status_completed")]
    [InlineData("SELECT substr(created_utc, 1, 10) AS day, COUNT(*) FROM download_dispatches WHERE created_utc >= '2026-01-01' AND created_utc < '2026-02-01' GROUP BY day;", "ix_download_dispatches_created_covering")]
    [InlineData("SELECT indexer_id, COUNT(*), AVG(elapsed_ms), SUM(candidate_count), SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), SUM(CASE WHEN query_kind = 'rss' THEN 1 ELSE 0 END) FROM indexer_query_events WHERE created_utc >= '2026-01-01' AND created_utc < '2026-02-01' GROUP BY indexer_id;", "ix_indexer_query_events_covering")]
    [InlineData("SELECT MAX(indexer_name), COUNT(*), SUM(CASE WHEN grab_status = 'succeeded' THEN 1 ELSE 0 END), MAX(indexer_id) FROM download_dispatches WHERE created_utc >= '2026-01-01' AND created_utc < '2026-02-01' AND status != 'archived' GROUP BY COALESCE(indexer_id, 'name:' || LOWER(indexer_name));", "ix_download_dispatches_created_covering")]
    public async Task A_jobs_screen_query_reads_its_own_index(string sql, string expectedIndex)
    {
        using var storage = TestStorage.Create();
        await InitializeJobsAsync(storage);

        var plan = await PlanAsync(storage, DelunoDatabaseNames.Jobs, sql);

        Assert.Contains(expectedIndex, plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT id FROM job_queue ORDER BY created_utc DESC, id DESC LIMIT 81;")]
    [InlineData("SELECT id FROM download_dispatches WHERE status != 'archived' ORDER BY COALESCE(grab_attempted_utc, created_utc) DESC, id DESC LIMIT 51;")]
    [InlineData("SELECT id FROM activity_events WHERE related_entity_type = 'movie' ORDER BY created_utc DESC, id DESC LIMIT 51;")]
    public async Task A_newest_first_list_is_never_sorted_after_the_fact(string sql)
    {
        using var storage = TestStorage.Create();
        await InitializeJobsAsync(storage);

        var plan = await PlanAsync(storage, DelunoDatabaseNames.Jobs, sql);

        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pick_of_a_titles_wanted_state_row_needs_no_sort_and_the_daily_counts_read_an_index()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        await new Deluno.Movies.Data.MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<Deluno.Movies.Data.MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new Deluno.Series.Data.SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<Deluno.Series.Data.SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var moviePick = await PlanAsync(storage, DelunoDatabaseNames.Movies,
            "SELECT pick.rowid FROM movie_wanted_state pick WHERE pick.movie_id = 'm' ORDER BY pick.has_file DESC, pick.not_wanted_here ASC, pick.quality_cutoff_met ASC, pick.library_id ASC LIMIT 1;");
        var seriesPick = await PlanAsync(storage, DelunoDatabaseNames.Series,
            "SELECT pick.rowid FROM series_wanted_state pick WHERE pick.series_id = 's' ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC LIMIT 1;");
        var movieSearches = await PlanAsync(storage, DelunoDatabaseNames.Movies,
            "SELECT substr(created_utc, 1, 10) AS day, COUNT(*) FROM movie_search_history WHERE outcome = 'matched' AND created_utc >= 'a' AND created_utc < 'b' GROUP BY day;");
        var seriesSearches = await PlanAsync(storage, DelunoDatabaseNames.Series,
            "SELECT substr(created_utc, 1, 10) AS day, COUNT(*) FROM series_search_history WHERE outcome = 'matched' AND created_utc >= 'a' AND created_utc < 'b' GROUP BY day;");

        Assert.Contains("ix_movie_wanted_state_pick", moviePick, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", moviePick, StringComparison.Ordinal);
        Assert.Contains("ix_series_wanted_state_pick", seriesPick, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", seriesPick, StringComparison.Ordinal);
        Assert.Contains("ix_movie_search_history_created_outcome", movieSearches, StringComparison.Ordinal);
        Assert.Contains("ix_series_search_history_created_outcome", seriesSearches, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ payloads and raw JSON

    [Fact]
    public async Task A_job_list_leaves_the_payload_out_unless_asked_and_the_one_job_route_always_has_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var small = await EnqueueAsync(app, "media.probe", """{"file":"a.mkv"}""");
        var huge = await EnqueueAsync(app, "library.search.movies", "{\"torrentFile\":\"" + new string('A', 200_000) + "\"}");

        var plain = await ItemsAsync(app, "/api/jobs?pageSize=10");
        var asked = await ItemsAsync(app, "/api/jobs?pageSize=10&includePayload=true");

        Assert.All(plain, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("payloadJson").ValueKind));
        Assert.Equal("""{"file":"a.mkv"}""", Find(asked, small).GetProperty("payloadJson").GetString());
        // Asked for, but a payload that big is still not sent in a list.
        Assert.Equal(JsonValueKind.Null, Find(asked, huge).GetProperty("payloadJson").ValueKind);

        var whole = await app.Client.GetFromJsonAsync<JsonElement>($"/api/jobs/{huge}");
        Assert.Equal(200_000 + "{\"torrentFile\":\"\"}".Length, whole.GetProperty("payloadJson").GetString()!.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/jobs/no-such-job")).StatusCode);
    }

    [Fact]
    public async Task All_the_failed_jobs_come_back_in_one_call()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var ids = new List<string>();
        for (var index = 0; index < 130; index++)
        {
            var id = await EnqueueAsync(app, "media.probe", $"{{\"n\":{index}}}");
            await SetStatusAsync(app, id, index % 2 == 0 ? "failed" : "dead-letter");
            ids.Add(id);
        }

        var response = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobs?failed=true&pageSize=500");

        Assert.Equal(130, response.GetProperty("items").GetArrayLength());
        Assert.False(response.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task A_dispatch_list_leaves_out_a_large_grab_record_and_the_detail_route_keeps_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var big = "{\"searchPlan\":\"" + new string('p', 200_000) + "\"}";
        const string small = "{\"message\":\"ok\"}";
        await InsertDispatchAsync(app, "dispatch-big", "2026-08-02T00:00:00.0000000+00:00", big);
        await InsertDispatchAsync(app, "dispatch-small", "2026-08-01T00:00:00.0000000+00:00", small);

        var list = await ItemsAsync(app, "/api/download-dispatches?pageSize=10");
        var withRaw = await ItemsAsync(app, "/api/download-dispatches?pageSize=10&includeRawJson=true");

        Assert.Equal(JsonValueKind.Null, Find(list, "dispatch-big").GetProperty("notesJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, Find(list, "dispatch-big").GetProperty("grabResponseJson").ValueKind);
        Assert.Equal(small, Find(list, "dispatch-small").GetProperty("notesJson").GetString());
        Assert.Equal(big, Find(withRaw, "dispatch-big").GetProperty("notesJson").GetString());

        var detail = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/download-dispatches/dispatch-big");
        Assert.Equal(big, JsonSerializer.Serialize(detail).Contains("searchPlan") ? big : null);
    }

    [Fact]
    public void A_long_search_plan_is_cut_to_its_first_ten_and_says_how_many_there_were()
    {
        var plan = "{\"searchPlan\":{\"candidates\":["
            + string.Join(",", Enumerable.Range(0, 400).Select(index => $"{{\"releaseName\":\"Release {index}\",\"reasons\":\"{new string('r', 200)}\"}}"))
            + "]},\"episodeIds\":[" + string.Join(",", Enumerable.Range(0, 40).Select(index => $"\"episode-{index}\"")) + "]}";

        var trimmed = DispatchNotesTrimmer.Trim(plan)!;

        using var document = JsonDocument.Parse(trimmed);
        var searchPlan = document.RootElement.GetProperty("searchPlan");
        Assert.Equal(10, searchPlan.GetProperty("candidates").GetArrayLength());
        Assert.Equal("Release 0", searchPlan.GetProperty("candidates")[0].GetProperty("releaseName").GetString());
        Assert.Equal(400, searchPlan.GetProperty("candidatesTotal").GetInt32());
        // The episodes a season pack covered are read back to search for each again.
        Assert.Equal(40, document.RootElement.GetProperty("episodeIds").GetArrayLength());
        Assert.True(trimmed.Length < plan.Length / 10);
    }

    [Fact]
    public void Notes_that_are_already_small_are_left_exactly_as_written()
    {
        const string notes = "{\"chosenOnSearch\":true,\"list\":[1,2,3,4,5,6,7,8,9,10,11,12]}";

        Assert.Equal(notes, DispatchNotesTrimmer.Trim(notes));
        Assert.Null(DispatchNotesTrimmer.Trim(null));
    }

    [Fact]
    public async Task A_new_grab_stores_its_notes_once_and_trimmed_and_keeps_its_decision_columns()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        await InitializeJobsAsync(storage);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var store = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), dispatches);
        var notes = "{\"decision\":{\"candidate\":{\"Quality\":\"WEB 1080p\",\"Score\":77}},\"searchPlan\":{\"candidates\":["
            + string.Join(",", Enumerable.Range(0, 300).Select(index => $"{{\"releaseName\":\"Release {index}\",\"why\":\"{new string('w', 400)}\"}}"))
            + "]}}";

        var id = await store.RecordDownloadDispatchAsync(
            libraryId: "movies-main", mediaType: "movies", entityType: "movie", entityId: "movie-1",
            releaseName: "Movie.2026.1080p-GROUP", indexerName: "Indexer", downloadClientId: "c", downloadClientName: "C",
            status: "sent", notesJson: notes, cancellationToken: CancellationToken.None);

        var stored = await dispatches.GetDispatchAsync(id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.NotesJson!.Length < notes.Length / 10, "The notes are stored trimmed.");
        Assert.Null(stored.GrabResponseJson);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        var quality = await ScalarAsync<string>(connection, $"SELECT decision_quality FROM download_dispatches WHERE id = '{id}';");
        var activity = await ScalarAsync<long>(connection, "SELECT MAX(length(details_json)) FROM activity_events;");
        Assert.Equal("WEB 1080p", quality);
        Assert.True(activity < notes.Length / 10, "The activity row carries the trimmed notes too.");
    }

    [Fact]
    public async Task The_cleanup_migration_removes_the_second_copy_and_cuts_an_oversized_plan_on_rows_already_stored()
    {
        using var storage = TestStorage.Create();
        await InitializeJobsAsync(storage);
        var plan = "{\"searchPlan\":{\"candidates\":["
            + string.Join(",", Enumerable.Range(0, 300).Select(index => $"{{\"releaseName\":\"Release {index}\",\"why\":\"{new string('w', 400)}\"}}"))
            + "]}}";
        const string small = "{\"message\":\"ok\"}";

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await ExecuteAsync(connection, "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, notes_json, grab_response_json, created_utc) " +
                $"VALUES ('big', 'l', 'movie', 'movie', 'e', 'r', 'i', 'c', 'C', 'sent', '{plan}', '{plan}', '2026-08-01T00:00:00Z');");
            await ExecuteAsync(connection, "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, notes_json, grab_response_json, created_utc) " +
                $"VALUES ('small', 'l', 'movie', 'movie', 'e', 'r', 'i', 'c', 'C', 'sent', '{small}', '{{\"code\":200}}', '2026-08-01T00:00:00Z');");
            await ExecuteAsync(connection, $"INSERT INTO activity_events (id, category, message, details_json, created_utc) VALUES ('a', 'grab', 'm', '{plan}', '2026-08-01T00:00:00Z');");

            await using var transaction = await connection.BeginTransactionAsync();
            await new V0040TrimOversizedGrabNotes().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();

            Assert.Equal(1L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM download_dispatches WHERE id = 'big' AND grab_response_json IS NULL;"));
            Assert.True(await ScalarAsync<long>(connection, "SELECT length(notes_json) FROM download_dispatches WHERE id = 'big';") < 65536);
            Assert.Equal(10L, await ScalarAsync<long>(connection, "SELECT json_array_length(notes_json, '$.searchPlan.candidates') FROM download_dispatches WHERE id = 'big';"));
            Assert.Equal(300L, await ScalarAsync<long>(connection, "SELECT json_extract(notes_json, '$.searchPlan.candidatesTotal') FROM download_dispatches WHERE id = 'big';"));
            Assert.True(await ScalarAsync<long>(connection, "SELECT length(details_json) FROM activity_events WHERE id = 'a';") < 65536);
            // What was small, and what was not a copy, is untouched.
            Assert.Equal(small, await ScalarAsync<string>(connection, "SELECT notes_json FROM download_dispatches WHERE id = 'small';"));
            Assert.Equal("{\"code\":200}", await ScalarAsync<string>(connection, "SELECT grab_response_json FROM download_dispatches WHERE id = 'small';"));
        }
    }

    // ------------------------------------------------------------------ catalogue counts

    [Fact]
    public async Task The_catalogue_can_answer_how_many_without_counting_the_chips()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedMoviesAsync(app, 6);

        var withChips = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?pageSize=2");
        var withoutChips = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?pageSize=2&facets=false");
        var filtered = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?pageSize=2&facets=false&status=missing");
        var filteredWithChips = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?pageSize=2&status=missing");

        Assert.Equal(6, withChips.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Object, withChips.GetProperty("facets").ValueKind);
        Assert.Equal(6, withoutChips.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, withoutChips.GetProperty("facets").ValueKind);
        Assert.Equal(filteredWithChips.GetProperty("totalCount").GetInt32(), filtered.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, withoutChips.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_chip_counts_are_kept_between_calls_and_never_outlive_a_write()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedMoviesAsync(app, 4);

        Assert.Equal(4, await AllChipAsync(app));
        Assert.Equal(4, await AllChipAsync(app));

        // A write from anywhere in the process, by any connection, moves the answer at once.
        await SeedMoviesAsync(app, 1, startAt: 4);
        Assert.Equal(5, await AllChipAsync(app));

        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            await ExecuteAsync(connection, "DELETE FROM movie_entries WHERE id = 'movie-000000';");
        }

        Assert.Equal(4, await AllChipAsync(app));
    }

    [Fact]
    public async Task A_write_moves_the_generation_and_a_read_does_not()
    {
        using var storage = TestStorage.Create();
        await InitializeJobsAsync(storage);
        var before = storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Jobs);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM job_queue;");
        }

        Assert.Equal(before, storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Jobs));

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await ExecuteAsync(connection, "INSERT INTO worker_heartbeats (worker_id, last_seen_utc) VALUES ('w', '2026-08-01T00:00:00Z');");
        }

        Assert.True(storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Jobs) > before);
        Assert.Equal(0, storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Movies));
    }

    // ------------------------------------------------------------------ the keep-for-a-while memo

    [Fact]
    public async Task The_memo_computes_once_until_the_generation_moves_or_the_time_is_up()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        var memo = new GenerationMemo<int>(clock);
        var computed = 0;
        Task<int> Compute() => Task.FromResult(++computed);

        Assert.Equal(1, await memo.GetOrAddAsync("k", 7, TimeSpan.FromSeconds(10), Compute));
        Assert.Equal(1, await memo.GetOrAddAsync("k", 7, TimeSpan.FromSeconds(10), Compute));
        Assert.Equal(2, await memo.GetOrAddAsync("k", 8, TimeSpan.FromSeconds(10), Compute));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(3, await memo.GetOrAddAsync("k", 8, TimeSpan.FromSeconds(10), Compute));
        Assert.Equal(4, await memo.GetOrAddAsync("other", 8, TimeSpan.FromSeconds(10), Compute));
    }

    [Fact]
    public async Task Callers_arriving_together_share_one_computation_and_a_failure_is_not_kept()
    {
        var memo = new GenerationMemo<int>();
        var computed = 0;
        var gate = new TaskCompletionSource();
        Task<int> Slow() => Task.Run(async () => { Interlocked.Increment(ref computed); await gate.Task; return 5; });

        var first = memo.GetOrAddAsync("k", 0, TimeSpan.FromMinutes(1), Slow);
        var second = memo.GetOrAddAsync("k", 0, TimeSpan.FromMinutes(1), Slow);
        gate.SetResult();
        Assert.Equal(new[] { 5, 5 }, await Task.WhenAll(first, second));
        Assert.Equal(1, computed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memo.GetOrAddAsync("bad", 0, TimeSpan.FromMinutes(1), () => Task.FromException<int>(new InvalidOperationException("no"))));
        Assert.Equal(9, await memo.GetOrAddAsync("bad", 0, TimeSpan.FromMinutes(1), () => Task.FromResult(9)));
    }

    [Fact]
    public async Task The_dashboard_series_is_kept_for_thirty_seconds()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));

        var first = await CompletedJobsTodayAsync(app);
        var jobId = await EnqueueAsync(app, "media.probe", "{}");
        await SetStatusAsync(app, jobId, "completed", clock.GetUtcNow());

        Assert.Equal(first, await CompletedJobsTodayAsync(app));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(first + 1, await CompletedJobsTodayAsync(app));
    }

    // ------------------------------------------------------------------ import recovery

    [Fact]
    public async Task Import_recovery_lists_the_newest_fifty_without_details_and_still_counts_them_all()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            for (var index = 0; index < 60; index++)
            {
                await ExecuteAsync(connection,
                    "INSERT INTO movie_import_recovery_cases (id, title, failure_kind, summary, recommended_action, details_json, detected_utc, status) " +
                    $"VALUES ('case-{index:D3}', 'Case {index}', 'unmatched', 'No match', 'Pick', '{{\"files\":\"x\"}}', '2026-08-01T00:{index:D2}:00.0000000+00:00', 'open');");
            }
        }

        var plain = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/import-recovery");
        var everything = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/import-recovery?limit=500&includeDetails=true");
        var countsOnly = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/import-recovery?limit=0");

        Assert.Equal(60, plain.GetProperty("openCount").GetInt32());
        Assert.Equal(50, plain.GetProperty("recentCases").GetArrayLength());
        Assert.Equal("case-059", plain.GetProperty("recentCases")[0].GetProperty("id").GetString());
        Assert.All(plain.GetProperty("recentCases").EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("detailsJson").ValueKind));
        Assert.Equal(60, everything.GetProperty("recentCases").GetArrayLength());
        Assert.Equal("{\"files\":\"x\"}", everything.GetProperty("recentCases")[0].GetProperty("detailsJson").GetString());
        Assert.Equal(60, countsOnly.GetProperty("openCount").GetInt32());
        Assert.Equal(0, countsOnly.GetProperty("recentCases").GetArrayLength());
    }

    // ------------------------------------------------------------------ helpers

    private static async Task InitializeJobsAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static async Task<string> PlanAsync(TestStorage storage, string database, string sql)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> EnqueueAsync(ApplicationTestHost app, string jobType, string payload)
    {
        var store = app.Services.GetRequiredService<Deluno.Jobs.Data.IJobScheduler>();
        var job = await store.EnqueueAsync(
            new Deluno.Jobs.Contracts.EnqueueJobRequest(jobType, "test", payload, null, null, null, IdempotencyKey: Guid.NewGuid().ToString("N")),
            CancellationToken.None);
        return job.Id;
    }

    private static async Task SetStatusAsync(ApplicationTestHost app, string jobId, string status, DateTimeOffset? completed = null)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        await ExecuteAsync(connection,
            $"UPDATE job_queue SET status = '{status}', last_error = 'boom', completed_utc = '{(completed ?? DateTimeOffset.UtcNow):O}' WHERE id = '{jobId}';");
    }

    private static async Task InsertDispatchAsync(ApplicationTestHost app, string id, string createdUtc, string notes)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, notes_json, grab_response_json, created_utc) " +
            "VALUES ($id, 'l', 'movie', 'movie', 'e', 'r', 'i', 'c', 'C', 'sent', $notes, $notes, $created);";
        foreach (var (name, value) in new[] { ("$id", id), ("$notes", notes), ("$created", createdUtc) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedMoviesAsync(ApplicationTestHost app, int count, int startAt = 0)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        for (var index = startAt; index < startAt + count; index++)
        {
            await ExecuteAsync(connection,
                $"INSERT INTO movie_entries (id, title, release_year, monitored, created_utc, updated_utc) VALUES ('movie-{index:D6}', 'Title {index}', 2000, 1, '2026-08-27T00:00:00.0000000+00:00', '2026-08-27T00:00:00.0000000+00:00');");
            await ExecuteAsync(connection,
                $"INSERT INTO movie_wanted_state (movie_id, library_id, wanted_status, wanted_reason, has_file, updated_utc) VALUES ('movie-{index:D6}', 'library-films', 'missing', 'seeded', 0, '2026-08-27T00:00:00.0000000+00:00');");
        }
    }

    private static async Task<int> AllChipAsync(ApplicationTestHost app)
    {
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?pageSize=2");
        return page.GetProperty("facets").GetProperty("all").GetInt32();
    }

    private static async Task<int> CompletedJobsTodayAsync(ApplicationTestHost app)
    {
        var metrics = await app.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/metrics?days=3");
        var completed = metrics.GetProperty("jobs").GetProperty("succeeded");
        return completed.EnumerateArray().Sum(day => day.GetProperty("value").GetInt32());
    }

    private static async Task<JsonElement[]> ItemsAsync(ApplicationTestHost app, string path)
    {
        var response = await app.Client.GetFromJsonAsync<JsonElement>(path);
        return response.GetProperty("items").EnumerateArray().ToArray();
    }

    private static JsonElement Find(IEnumerable<JsonElement> items, string id)
        => items.Single(item => item.GetProperty("id").GetString() == id);
}
