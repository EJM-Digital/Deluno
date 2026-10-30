using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Dashboard;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Dashboard's Activity, Downloads and System sections and its daily metrics (#1329), read over HTTP from stored rows in real,
/// migrated SQLite files: the windowed Activity counts and the eight newest rows, the client rows of the Downloads panel, the
/// outage, job counts and lanes of the System section, and the daily series of titles added and searches.
/// </summary>
public sealed class DashboardLowRowsTests
{
    private sealed class OutageMonitor(DateTimeOffset since) : IConnectivityMonitor
    {
        public ConnectivityStatus Current { get; } = new(true, since, DateTimeOffset.UtcNow, [], "The internet looks down.");

        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
    }

    private sealed class MutableSnapshotStore(List<DownloadClientTelemetrySnapshot> clients, DownloadTelemetrySummary summary, DateTimeOffset capturedUtc) : IDownloadTelemetrySnapshotStore
    {
        private DownloadTelemetryCapture Capture => new(new DownloadTelemetryOverview(summary, [.. clients], capturedUtc), []);

        public DownloadTelemetryCapture? Current => Capture;

        public DateTimeOffset? CapturedUtc => capturedUtc;

        public void MarkStale()
        {
        }

        public Task<SnapshotEntry<DownloadTelemetryCapture>?> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken)
            => Task.FromResult<SnapshotEntry<DownloadTelemetryCapture>?>(new SnapshotEntry<DownloadTelemetryCapture>(Capture, capturedUtc));

        public Task<SnapshotEntry<DownloadTelemetryCapture>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
            => Task.FromResult(new SnapshotEntry<DownloadTelemetryCapture>(Capture, capturedUtc));
    }

    private static async Task<JsonElement> SummaryAsync(ApplicationTestHost app)
    {
        ((DashboardSummaryService)app.Services.GetRequiredService<IDashboardSummaryService>()).Invalidate();
        var response = await app.Client.GetAsync("/api/dashboard/summary");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task The_activity_section_counts_each_kind_inside_the_last_24_hours_and_lists_the_eight_newest_rows_with_plain_labels()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        string[] categories =
        [
            "movies.search.completed", "series.search.completed",
            "movies.release.grabbed", "series.release.force-grabbed", "series.release.grabbed",
            "filesystem.import.completed", "filesystem.import.season-pack.completed",
            "download.orphan.removed", "download.queue.removed", "download.sharing.reclaimed", "download.sharing.stopped",
            "retention.removed", "retention.removed", "retention.removed"
        ];
        foreach (var category in categories)
        {
            await feed.RecordActivityAsync(category, $"Something happened: {category}", null, null, null, null, CancellationToken.None);
        }

        // One of each outside the window must not count.
        var old = DateTimeOffset.UtcNow.AddHours(-30).ToString("O");
        await ApiScenes.ExecuteAsync(app, "jobs",
            $"""
            INSERT INTO activity_events (id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc) VALUES
                ('old1', 'movies.search.completed', 'Old search', NULL, NULL, NULL, NULL, '{old}'),
                ('old2', 'movies.release.grabbed', 'Old grab', NULL, NULL, NULL, NULL, '{old}'),
                ('old3', 'filesystem.import.completed', 'Old import', NULL, NULL, NULL, NULL, '{old}'),
                ('old4', 'download.queue.removed', 'Old cleanup', NULL, NULL, NULL, NULL, '{old}'),
                ('old5', 'retention.removed', 'Old retention', NULL, NULL, NULL, NULL, '{old}');
            """);

        var activity = (await SummaryAsync(app)).GetProperty("activity");

        Assert.Equal(24, activity.GetProperty("windowHours").GetInt32());
        Assert.Equal(2, activity.GetProperty("searchesRun").GetInt32());
        Assert.Equal(3, activity.GetProperty("downloadsStarted").GetInt32());
        Assert.Equal(2, activity.GetProperty("importsCompleted").GetInt32());
        Assert.Equal(4, activity.GetProperty("cleanupRemovals").GetInt32());
        Assert.Equal(3, activity.GetProperty("retentionRemovals").GetInt32());
        var recent = activity.GetProperty("recent").EnumerateArray().ToArray();
        Assert.Equal(8, recent.Length);
        foreach (var item in recent)
        {
            Assert.DoesNotContain(".", item.GetProperty("label").GetString()!);
            Assert.Equal(ActivityEventLabels.For(item.GetProperty("category").GetString()!), item.GetProperty("label").GetString());
        }

        Assert.DoesNotContain(recent, item => item.GetProperty("message").GetString()!.StartsWith("Old ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_downloads_panel_lists_each_switched_on_client_with_its_reading_and_a_client_with_no_reading_from_its_stored_health()
    {
        var capturedUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
        var held = new List<DownloadClientTelemetrySnapshot>();
        var store = new MutableSnapshotStore(held, new DownloadTelemetrySummary(1, 2, 0, 0, 0, 0, 1.5), capturedUtc);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IDownloadTelemetrySnapshotStore>(store));
        var names = new[] { "Read client", "Down client", "Unread client", "Switched off" };
        var ids = new List<string>();
        for (var index = 0; index < names.Length; index++)
        {
            var created = await app.Client.PostAsJsonAsync("/api/download-clients", new { name = names[index], protocol = "qbittorrent", host = "127.0.0.1", port = 9 + index, isEnabled = index < 3 });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        await ApiScenes.ExecuteAsync(app, "platform", $"UPDATE download_clients SET health_status = 'unreachable' WHERE id = '{ids[2]}'");
        DownloadClientTelemetrySnapshot Snapshot(string id, string name, bool failing) => new(
            id, name, "qbittorrent", "http://127.0.0.1:1", failing ? "unreachable" : "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
            new DownloadTelemetrySummary(3, 4, 0, 0, 0, 0, 0.5), [], [], capturedUtc)
        {
            LastFailure = failing
                ? new IntegrationFailure("download-client", id, name, "telemetry", IntegrationFailureKind.Unavailable, IntegrationRetryState.Retrying, "No answer.")
                : null,
            ClientPaused = failing ? null : true,
            ClientVersion = failing ? null : "5.0.1",
            ClientWarnings = failing ? null : ["Disk nearly full.", "An unpack failed."],
            FreeSpaceBytes = failing ? null : 123_456_789
        };

        held.Add(Snapshot(ids[0], names[0], failing: false));
        held.Add(Snapshot(ids[1], names[1], failing: true));

        var clients = (await SummaryAsync(app)).GetProperty("downloads").GetProperty("clients").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);

        Assert.Equal(3, clients.Count);
        Assert.DoesNotContain(ids[3], clients.Keys);
        var read = clients[ids[0]];
        Assert.True(read.GetProperty("reachable").GetBoolean());
        Assert.True(read.GetProperty("paused").GetBoolean());
        Assert.Equal("5.0.1", read.GetProperty("version").GetString());
        Assert.Equal(2, read.GetProperty("warnings").GetInt32());
        Assert.Equal(123_456_789, read.GetProperty("freeSpaceBytes").GetInt64());
        Assert.Equal(3, read.GetProperty("active").GetInt32());
        Assert.Equal(4, read.GetProperty("queued").GetInt32());
        var down = clients[ids[1]];
        Assert.False(down.GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, down.GetProperty("paused").ValueKind);
        Assert.Equal(JsonValueKind.Null, down.GetProperty("freeSpaceBytes").ValueKind);
        var unread = clients[ids[2]];
        Assert.False(unread.GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unread.GetProperty("paused").ValueKind);
        Assert.Equal(0, unread.GetProperty("warnings").GetInt32());
    }

    [Fact]
    public async Task The_system_section_carries_the_internet_outage_and_when_it_began_the_job_counts_and_every_lane_state()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-42);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IConnectivityMonitor>(new OutageMonitor(since)));
        var when = DateTimeOffset.UtcNow.ToString("O");
        await ApiScenes.ExecuteAsync(app, "jobs",
            $"""
            INSERT INTO job_queue (id, job_type, source, status, attempts, created_utc, scheduled_utc) VALUES
                ('r1', 'library.search.movies', 'test', 'running', 1, '{when}', '{when}'),
                ('q1', 'library.search.movies', 'test', 'queued', 0, '{when}', '{when}'),
                ('q2', 'library.search.movies', 'test', 'queued', 0, '{when}', '{when}'),
                ('f1', 'library.search.movies', 'test', 'failed', 3, '{when}', '{when}'),
                ('f2', 'library.search.movies', 'test', 'dead-letter', 3, '{when}', '{when}'),
                ('f3', 'library.search.movies', 'test', 'dead-letter', 3, '{when}', '{when}');
            """);
        var board = app.Services.GetRequiredService<JobLaneStateBoard>();
        board.Started("lane-idle");
        board.Started("lane-running");
        board.JobStarted("lane-running");
        board.Started("lane-failing");
        board.SetProblem("lane-failing", "The disk is full.");
        board.MarkRestarting("lane-restarting", "It crashed.");
        board.MarkDisabled("lane-disabled");

        var system = (await SummaryAsync(app)).GetProperty("system");

        Assert.True(system.GetProperty("internetOutage").GetBoolean());
        Assert.Equal(since.UtcDateTime, system.GetProperty("outageSinceUtc").GetDateTimeOffset().UtcDateTime, TimeSpan.FromSeconds(1));
        var jobs = system.GetProperty("jobs");
        Assert.Equal(1, jobs.GetProperty("running").GetInt32());
        Assert.Equal(2, jobs.GetProperty("queued").GetInt32());
        Assert.Equal(3, jobs.GetProperty("failed").GetInt32());
        var lanes = system.GetProperty("lanes").EnumerateArray().ToDictionary(lane => lane.GetProperty("name").GetString()!, lane => lane);
        Assert.Equal("idle", lanes["lane-idle"].GetProperty("state").GetString());
        Assert.Equal("running", lanes["lane-running"].GetProperty("state").GetString());
        Assert.Equal(1, lanes["lane-running"].GetProperty("activeJobs").GetInt32());
        Assert.Equal("failing", lanes["lane-failing"].GetProperty("state").GetString());
        Assert.Equal("The disk is full.", lanes["lane-failing"].GetProperty("lastError").GetString());
        Assert.Equal("restarting", lanes["lane-restarting"].GetProperty("state").GetString());
        Assert.Equal("disabled", lanes["lane-disabled"].GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(system.GetProperty("version").GetString()));
        Assert.True(system.GetProperty("uptimeSeconds").GetInt64() >= 0);
    }

    [Fact]
    public async Task The_daily_metrics_count_titles_added_and_searches_matched_or_not_by_the_day_they_were_stored_and_the_library_size_runs_up()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Metric Film", 2020);
        await ApiScenes.ShowOnDiskAsync(app, "Metric Show", 2019);
        var today = DateTimeOffset.UtcNow;
        var twoDaysAgo = today.AddDays(-2).ToString("O");
        await ApiScenes.ExecuteAsync(app, "movies",
            $"""
            INSERT INTO movie_search_history (id, movie_id, library_id, trigger_kind, outcome, release_name, indexer_name, details_json, created_utc) VALUES
                ('s1', '{film.MovieId}', '{film.LibraryId}', 'automatic', 'matched', NULL, NULL, NULL, '{today:O}'),
                ('s2', '{film.MovieId}', '{film.LibraryId}', 'automatic', 'no-match', NULL, NULL, NULL, '{today:O}'),
                ('s3', '{film.MovieId}', '{film.LibraryId}', 'automatic', 'no-match', NULL, NULL, NULL, '{twoDaysAgo}');
            """);

        var metrics = await app.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/metrics?days=5");

        static int[] Values(JsonElement series) => [.. series.EnumerateArray().Select(day => day.GetProperty("value").GetInt32())];
        Assert.Equal([0, 0, 0, 0, 2], Values(metrics.GetProperty("titlesAdded")));
        Assert.Equal([0, 0, 0, 0, 2], Values(metrics.GetProperty("librarySize")));
        Assert.Equal([0, 0, 0, 0, 1], Values(metrics.GetProperty("searches").GetProperty("succeeded")));
        Assert.Equal([0, 0, 1, 0, 1], Values(metrics.GetProperty("searches").GetProperty("failed")));
    }
}
