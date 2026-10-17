using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Worker.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Dashboard's summary sections (#1282), each read over HTTP from rows seeded into a real, migrated set of SQLite
/// files on disk: the library totals, the pipeline's day and running figures, the imports and processors counts, the
/// automation section and the attention count. Nothing is stood in for.
/// </summary>
public sealed class DashboardSummarySectionsFromStoredRowsTests
{
    private static async Task<JsonElement> SummaryAsync(ApplicationTestHost app)
    {
        ((DashboardSummaryService)app.Services.GetRequiredService<IDashboardSummaryService>()).Invalidate();
        var response = await app.Client.GetAsync("/api/dashboard/summary");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task ExecuteAsync(ApplicationTestHost app, string database, string sql)
    {
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Iso(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_library_section_counts_each_wanted_state_of_the_monitored_films_and_leaves_an_unmonitored_one_out_of_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraries = app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var library = await libraries.CreateLibraryAsync(NewLibrary("Films", "movies", "/data/films"), CancellationToken.None);

        // Two monitored films in each state, and a ninth that is missing but not monitored.
        string[] states = [WantedStatuses.Missing, WantedStatuses.Upgrade, WantedStatuses.Covered, WantedStatuses.Upcoming];
        for (var index = 0; index < 9; index++)
        {
            var movie = await movies.AddAsync(new CreateMovieRequest($"Film {index}", 2000 + index, null, index < 8, library.Id), CancellationToken.None);
            var status = states[index % states.Length];
            var hasFile = status is WantedStatuses.Covered or WantedStatuses.Upgrade;
            await movies.EnsureWantedStateAsync(movie.Id, library.Id, status, "test-seed", hasFile, hasFile ? "WEBDL-1080p" : null, "WEBDL-1080p", status == WantedStatuses.Covered, CancellationToken.None);
        }

        var counts = (await SummaryAsync(app)).GetProperty("library").GetProperty("movies");

        Assert.Equal(9, counts.GetProperty("total").GetInt32());
        Assert.Equal(8, counts.GetProperty("monitored").GetInt32());
        Assert.Equal(1, counts.GetProperty("unmonitored").GetInt32());
        Assert.Equal(2, counts.GetProperty("missing").GetInt32());
        Assert.Equal(2, counts.GetProperty("upgradable").GetInt32());
        Assert.Equal(2, counts.GetProperty("qualityMet").GetInt32());
        Assert.Equal(2, counts.GetProperty("upcoming").GetInt32());
        Assert.Equal(0, counts.GetProperty("downloading").GetInt32());
        Assert.Equal(0, (await SummaryAsync(app)).GetProperty("library").GetProperty("shows").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_pipeline_section_counts_today_apart_from_all_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var now = DateTimeOffset.UtcNow;
        var today = now.AddMinutes(-30);
        var twoDaysAgo = now.AddDays(-2);

        // today: grabbed and detected 10 minutes later, imported 20 minutes after that.
        await Dispatch(app, "d1", created: today, grab: "succeeded", attempted: today, detected: today.AddMinutes(10), import: "imported", completed: today.AddMinutes(30));
        // today: a grab that failed.
        await Dispatch(app, "d2", created: today, grab: "failed", attempted: today, detected: null, import: null, completed: null);
        // two days ago: grabbed, detected 30 minutes later, and an import that failed.
        await Dispatch(app, "d3", created: twoDaysAgo, grab: "succeeded", attempted: twoDaysAgo, detected: twoDaysAgo.AddMinutes(30), import: "failed", completed: twoDaysAgo.AddMinutes(40));

        var pipeline = (await SummaryAsync(app)).GetProperty("pipeline");

        var day = pipeline.GetProperty("today");
        Assert.Equal(1, day.GetProperty("grabbed").GetInt32());
        Assert.Equal(1, day.GetProperty("detected").GetInt32());
        Assert.Equal(1, day.GetProperty("imported").GetInt32());
        Assert.Equal(1, day.GetProperty("failed").GetInt32());

        var totals = pipeline.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("grabbed").GetInt32());
        Assert.Equal(2, totals.GetProperty("detected").GetInt32());
        Assert.Equal(1, totals.GetProperty("imported").GetInt32());
        Assert.Equal(1, totals.GetProperty("failedGrabs").GetInt32());
        Assert.Equal(1, totals.GetProperty("failedImports").GetInt32());

    }

    // DEFECT: SqliteDispatchMetricsRepository.GetAverageDurationAsync reads AVG(...) with "is long" / "is int", but SQLite's AVG
    // is a REAL, so the scalar arrives as a double, matches neither and the average is always null: the Dashboard never shows
    // the average grab-to-detection or detection-to-import time, whatever the dispatches hold. Seen here: both stay null.
    [Fact]
    public async Task The_pipeline_section_averages_the_minutes_between_grab_and_detection_and_between_detection_and_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var now = DateTimeOffset.UtcNow;
        var today = now.AddMinutes(-30);
        var twoDaysAgo = now.AddDays(-2);
        await Dispatch(app, "d1", created: today, grab: "succeeded", attempted: today, detected: today.AddMinutes(10), import: "imported", completed: today.AddMinutes(30));
        await Dispatch(app, "d3", created: twoDaysAgo, grab: "succeeded", attempted: twoDaysAgo, detected: twoDaysAgo.AddMinutes(30), import: "failed", completed: twoDaysAgo.AddMinutes(40));

        var pipeline = (await SummaryAsync(app)).GetProperty("pipeline");

        // (10 + 30) / 2 minutes between grab and detection; (20 + 10) / 2 between detection and import.
        Assert.Equal(20 * 60, pipeline.GetProperty("averageGrabToDetectionSeconds").GetDouble(), 1);
        Assert.Equal(15 * 60, pipeline.GetProperty("averageDetectionToImportSeconds").GetDouble(), 1);
    }

    [Fact]
    public async Task The_imports_section_counts_open_recovery_cases_by_kind_across_films_and_shows_and_the_failed_jobs_and_hand_offs()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var when = Iso(DateTimeOffset.UtcNow);
        string Case(string table, string id, string kind, string status) =>
            $"INSERT INTO {table} (id, title, failure_kind, summary, recommended_action, detected_utc, status) VALUES ('{id}', 'T {id}', '{kind}', 's', 'a', '{when}', '{status}');";
        await ExecuteAsync(app, DelunoDatabaseNames.Movies,
            Case("movie_import_recovery_cases", "m1", "unmatched", "open") + Case("movie_import_recovery_cases", "m2", "unmatched", "open") + Case("movie_import_recovery_cases", "m3", "unmatched", "resolved"));
        await ExecuteAsync(app, DelunoDatabaseNames.Series,
            Case("series_import_recovery_cases", "s1", "unmatched", "open") + Case("series_import_recovery_cases", "s2", "quality-blocked", "open"));
        await ExecuteAsync(app, DelunoDatabaseNames.Jobs,
            $"""
            INSERT INTO job_queue (id, job_type, source, status, attempts, created_utc, scheduled_utc) VALUES
                ('j1', 'filesystem.import.execute', 'test', 'failed', 3, '{when}', '{when}'),
                ('j2', 'filesystem.import.execute', 'test', 'dead-letter', 3, '{when}', '{when}'),
                ('j3', 'filesystem.import.execute', 'test', 'queued', 0, '{when}', '{when}'),
                ('j4', 'library.search.movies', 'test', 'failed', 3, '{when}', '{when}');
            """);
        var statuses = new[] { ProcessorHandoffStatuses.Failed, ProcessorHandoffStatuses.Waiting, ProcessorHandoffStatuses.Completed };
        var handoffs = statuses.Select((status, index) => $"('h{index}', 'lib', 'movies', 'c', 'q{index}', 'R{index}', '/r{index}', 'k{index}', 'Weir', NULL, '{status}', '{when}', '{when}')");
        await ExecuteAsync(app, DelunoDatabaseNames.Platform,
            "INSERT INTO processor_handoffs (id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key, processor_name, processor_connection_id, status, created_utc, updated_utc) VALUES " + string.Join(", ", handoffs) + ";");

        var summary = await SummaryAsync(app);
        var imports = summary.GetProperty("imports");

        Assert.Equal(4, imports.GetProperty("recoveryOpen").GetInt32());
        Assert.Equal(3, imports.GetProperty("recoveryByKind").GetProperty("unmatched").GetInt32());
        Assert.Equal(1, imports.GetProperty("recoveryByKind").GetProperty("quality-blocked").GetInt32());
        Assert.Equal(2, imports.GetProperty("failedImportJobs").GetInt32());
        Assert.Equal(1, imports.GetProperty("failedProcessorHandoffs").GetInt32());
        var processors = summary.GetProperty("processors");
        Assert.Equal(1, processors.GetProperty("waiting").GetInt32());
        Assert.Equal(3, processors.GetProperty("handoffsToday").GetInt32());
    }

    [Fact]
    public async Task The_automation_section_carries_each_librarys_search_status_the_last_cycle_and_every_task_with_its_last_result_and_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var now = DateTimeOffset.UtcNow;
        var next = Iso(now.AddMinutes(30));
        var last = Iso(now.AddMinutes(-20));
        await ExecuteAsync(app, DelunoDatabaseNames.Jobs,
            $"""
            INSERT INTO library_automation_state (library_id, library_name, media_type, status, search_requested, last_completed_utc, next_search_utc, last_error, updated_utc)
            VALUES ('lib-a', 'Films', 'movies', 'running', 1, '{last}', '{next}', NULL, '{last}'),
                   ('lib-b', 'Shows', 'tv', 'failed', 0, '{last}', NULL, 'The indexer did not answer.', '{last}');
            INSERT INTO search_cycle_runs (id, library_id, library_name, media_type, trigger_kind, status, planned_count, queued_count, skipped_count, started_utc, completed_utc)
            VALUES ('cycle-old', 'lib-a', 'Films', 'movies', 'scheduled', 'completed', 9, 9, 0, '{Iso(now.AddHours(-5))}', '{Iso(now.AddHours(-5))}'),
                   ('cycle-new', 'lib-a', 'Films', 'movies', 'scheduled', 'completed', 12, 7, 5, '{Iso(now.AddMinutes(-25))}', '{last}');
            INSERT INTO worker_schedule_state (schedule_key, last_run_utc, last_completed_utc, last_result, last_duration_ms, next_run_utc, interval_seconds, last_error)
            VALUES ('{SystemTasks.Backup}', '{last}', '{last}', 'failed', 1500, '{next}', 3600, 'The backup folder is not writable.');
            """);

        var automation = (await SummaryAsync(app)).GetProperty("automation");

        var libraries = automation.GetProperty("libraries").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        Assert.Equal("running", libraries["Films"].GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, libraries["Films"].GetProperty("nextSearchUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, libraries["Films"].GetProperty("lastCompletedUtc").ValueKind);
        Assert.Equal("failed", libraries["Shows"].GetProperty("status").GetString());
        Assert.Equal("The indexer did not answer.", libraries["Shows"].GetProperty("lastError").GetString());

        var cycle = automation.GetProperty("lastSearchCycle");
        Assert.Equal(12, cycle.GetProperty("planned").GetInt32());
        Assert.Equal(7, cycle.GetProperty("queued").GetInt32());
        Assert.Equal(5, cycle.GetProperty("skipped").GetInt32());

        var tasks = automation.GetProperty("tasks").EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!);
        Assert.Equal(SystemTasks.All.Count, tasks.Count);
        Assert.Equal("failed", tasks[SystemTasks.Backup].GetProperty("lastResult").GetString());
        Assert.Equal("The backup folder is not writable.", tasks[SystemTasks.Backup].GetProperty("lastError").GetString());
        Assert.NotEqual(JsonValueKind.Null, tasks[SystemTasks.Backup].GetProperty("nextRunUtc").ValueKind);
        Assert.Equal("not-run", tasks[SystemTasks.RssSync].GetProperty("lastResult").GetString());
        Assert.Equal(JsonValueKind.Null, tasks[SystemTasks.RssSync].GetProperty("lastError").ValueKind);
    }

    private static Task Dispatch(ApplicationTestHost app, string id, DateTimeOffset created, string grab, DateTimeOffset attempted, DateTimeOffset? detected, string? import, DateTimeOffset? completed)
    {
        string Sql(DateTimeOffset? value) => value is null ? "NULL" : $"'{Iso(value.Value)}'";
        string Text(string? value) => value is null ? "NULL" : $"'{value}'";
        return ExecuteAsync(app, DelunoDatabaseNames.Jobs,
            $"""
            INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc, grab_status, grab_attempted_utc, detected_utc, import_status, import_completed_utc)
            VALUES ('{id}', 'lib', 'movies', 'movie', 'e-{id}', 'Release.{id}', 'Idx', 'c1', 'Client', 'sent', '{Iso(created)}', {Text(grab)}, {Sql(attempted)}, {Sql(detected)}, {Text(import)}, {Sql(completed)});
            """);
    }

    private static Deluno.Libraries.Contracts.CreateLibraryRequest NewLibrary(string name, string mediaType, string root)
        => new(
            Name: name, MediaType: mediaType, Purpose: "General", RootPath: root, DownloadsPath: null, QualityProfileId: null,
            ImportWorkflow: null, ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null, ProcessorFailureMode: null,
            AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: null, RetryDelayHours: null, MaxItemsPerRun: null);
}
