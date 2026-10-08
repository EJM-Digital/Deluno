using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>A snapshot store that already holds one capture and never asks a client.</summary>
internal sealed class HeldSnapshotStore(DownloadTelemetryCapture capture, DateTimeOffset capturedUtc) : IDownloadTelemetrySnapshotStore
{
    public DownloadTelemetryCapture? Current => capture;

    public DateTimeOffset? CapturedUtc => capturedUtc;

    public Task<SnapshotEntry<DownloadTelemetryCapture>?> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken)
        => Task.FromResult<SnapshotEntry<DownloadTelemetryCapture>?>(new SnapshotEntry<DownloadTelemetryCapture>(capture, capturedUtc));

    public Task<SnapshotEntry<DownloadTelemetryCapture>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
        => Task.FromResult(new SnapshotEntry<DownloadTelemetryCapture>(capture, capturedUtc));

    public void MarkStale()
    {
    }
}

/// <summary>
/// GET /api/dashboard/summary (#1082, #1081): one compact response for every
/// Dashboard panel, from stored rows and in-memory readings only.
/// </summary>
public sealed class DashboardSummaryApiTests(ITestOutputHelper output)
{
    private static readonly string[] SectionNames =
    [
        "library", "downloads", "pipeline", "imports", "automation", "indexers", "processors",
        "retention", "subtitles", "requests", "blocklist", "health", "storage", "system", "activity"
    ];

    private static async Task<JsonElement> GetSummaryAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync("/api/dashboard/summary");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void Refresh(ApplicationTestHost app)
        => ((DashboardSummaryService)app.Services.GetRequiredService<IDashboardSummaryService>()).Invalidate();

    [Fact]
    public async Task An_empty_install_answers_with_every_section_and_plain_zeros()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var summary = await GetSummaryAsync(app);

        Assert.Equal(0, summary.GetProperty("unavailableSections").GetArrayLength());
        foreach (var name in SectionNames)
        {
            Assert.Equal(JsonValueKind.Object, summary.GetProperty(name).ValueKind);
            Assert.True(summary.GetProperty(name).TryGetProperty("asOfUtc", out _), $"{name} has no asOfUtc");
        }

        var library = summary.GetProperty("library");
        Assert.Equal(0, library.GetProperty("movies").GetProperty("total").GetInt32());
        Assert.Equal(0, library.GetProperty("shows").GetProperty("total").GetInt32());
        Assert.Equal(0, library.GetProperty("totalBytes").GetInt64());

        // Nothing has read a download client yet: that is "not measured", never "idle".
        var downloads = summary.GetProperty("downloads");
        Assert.False(downloads.GetProperty("hasReading").GetBoolean());
        Assert.Equal(JsonValueKind.Null, downloads.GetProperty("readingUtc").ValueKind);
        Assert.False(downloads.GetProperty("leftovers").GetProperty("scanned").GetBoolean());

        Assert.Equal(0, summary.GetProperty("health").GetProperty("problems").GetInt32());
        Assert.Equal(0, summary.GetProperty("health").GetProperty("topProblems").GetArrayLength());
        Assert.Equal(0, summary.GetProperty("blocklist").GetProperty("count").GetInt32());
        Assert.Equal(0, summary.GetProperty("subtitles").GetProperty("wanted").GetInt32());
        Assert.Equal(0, summary.GetProperty("retention").GetProperty("leavingSoon").GetInt32());
        Assert.Equal(0, summary.GetProperty("imports").GetProperty("recoveryOpen").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("automation").GetProperty("lastSearchCycle").ValueKind);
        Assert.False(summary.GetProperty("automation").GetProperty("queuePause").GetProperty("enabled").GetBoolean());
        Assert.True(summary.GetProperty("automation").GetProperty("tasks").GetArrayLength() > 0);
        Assert.False(summary.GetProperty("system").GetProperty("internetOutage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("system").GetProperty("machine").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("system").GetProperty("version").GetString()));
        Assert.Equal(0, summary.GetProperty("activity").GetProperty("searchesRun").GetInt32());
    }

    [Fact]
    public async Task The_summary_needs_the_read_scope_and_is_never_cached_by_the_browser()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var anonymous = await app.CreateAnonymousClient().GetAsync("/api/dashboard/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var response = await app.Client.GetAsync("/api/dashboard/summary");
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_source_that_fails_leaves_its_section_out_and_every_other_section_intact()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton<IHealthCheckRepository, ExplodingHealthRepository>();
        });

        var summary = await GetSummaryAsync(app);

        Assert.Equal(JsonValueKind.Null, summary.GetProperty("health").ValueKind);
        var unavailable = summary.GetProperty("unavailableSections").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["health"], unavailable);
        foreach (var name in SectionNames.Where(name => name != "health"))
        {
            Assert.Equal(JsonValueKind.Object, summary.GetProperty(name).ValueKind);
        }
    }

    [Fact]
    public async Task Building_the_summary_never_calls_a_download_client_or_probes_the_internet()
    {
        var telemetry = DispatchProxy.Create<IDownloadClientTelemetryService, CallCountingProxy>();
        var calls = (CallCountingProxy)(object)telemetry;
        var connectivity = new CountingConnectivityMonitor();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddScoped(_ => telemetry);
            services.AddSingleton<IConnectivityMonitor>(connectivity);
        });

        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name = "Test client",
            protocol = "qbittorrent",
            host = "127.0.0.1",
            port = 9,
            isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        calls.Calls = 0;
        connectivity.Probes = 0;

        var summary = await GetSummaryAsync(app);

        Assert.Equal(0, calls.Calls);
        Assert.Equal(0, connectivity.Probes);
        Assert.Equal(0, summary.GetProperty("unavailableSections").GetArrayLength());
        var clients = summary.GetProperty("downloads").GetProperty("clients");
        Assert.Equal(1, clients.GetArrayLength());
        Assert.Contains("qBittorrent", clients[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_held_download_snapshot_fills_the_downloads_panel_without_a_client_call()
    {
        // The same snapshot Downloads Now and /api/download-clients/telemetry read: held, never a live call,
        // and no progress publisher has run (#1082).
        var telemetry = DispatchProxy.Create<IDownloadClientTelemetryService, CallCountingProxy>();
        var calls = (CallCountingProxy)(object)telemetry;
        var capturedUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
        var summaryOf = new DownloadTelemetrySummary(
            ActiveCount: 3, QueuedCount: 4, CompletedCount: 1, StalledCount: 2, ProcessingCount: 5,
            ImportReadyCount: 6, TotalSpeedMbps: 12.3, WaitingForProcessorCount: 1, TotalUploadSpeedMbps: 0.5);
        var held = new HeldSnapshotStore(new DownloadTelemetryCapture(new DownloadTelemetryOverview(summaryOf, [], capturedUtc), []), capturedUtc);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddScoped(_ => telemetry);
            services.AddSingleton<IDownloadTelemetrySnapshotStore>(held);
        });

        var summary = await GetSummaryAsync(app);
        var downloads = summary.GetProperty("downloads");

        Assert.True(downloads.GetProperty("hasReading").GetBoolean());
        Assert.Equal(3, downloads.GetProperty("active").GetInt32());
        Assert.Equal(4, downloads.GetProperty("queued").GetInt32());
        Assert.Equal(2, downloads.GetProperty("stalled").GetInt32());
        Assert.Equal(5, downloads.GetProperty("processing").GetInt32());
        Assert.Equal(6, downloads.GetProperty("importReady").GetInt32());
        Assert.Equal(1, downloads.GetProperty("waitingForProcessor").GetInt32());
        Assert.Equal(12.3, downloads.GetProperty("downloadMbps").GetDouble(), 2);
        Assert.Equal(0, calls.Calls);
    }

    [Fact]
    public async Task Counts_that_no_repository_offered_read_the_stored_rows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();

        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            """
            INSERT INTO blocked_releases (id, release_key, release_name, indexer_name, media_type, reason_code, reason, blocked_utc)
            VALUES ('b1', 'k1', 'Bad.Release.1', 'Idx', 'movies', 'bad', 'bad', '2026-01-01T00:00:00.0000000+00:00'),
                   ('b2', 'k2', 'Bad.Release.2', 'Idx', 'movies', 'bad', 'bad', '2026-01-01T00:00:00.0000000+00:00');
            """);
        await ExecuteAsync(databases, DelunoDatabaseNames.Platform,
            """
            INSERT INTO retention_marks (rule_id, entity_id, level, title, first_matched_utc, remove_after_utc, state, updated_utc)
            VALUES ('r', 'm1', 'movie', 'Leaving A', '2026-09-01T00:00:00.0000000+00:00', '2026-10-05T00:00:00.0000000+00:00', 'leaving', '2026-09-01T00:00:00.0000000+00:00'),
                   ('r', 'm2', 'movie', 'Leaving B', '2026-09-01T00:00:00.0000000+00:00', '2026-10-01T00:00:00.0000000+00:00', 'leaving', '2026-09-01T00:00:00.0000000+00:00'),
                   ('r', 'm3', 'movie', 'Ready C', '2026-09-01T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00', 'ready', '2026-09-01T00:00:00.0000000+00:00');
            """);

        var summary = await GetSummaryAsync(app);

        Assert.Equal(2, summary.GetProperty("blocklist").GetProperty("count").GetInt32());
        var retention = summary.GetProperty("retention");
        Assert.Equal(2, retention.GetProperty("leavingSoon").GetInt32());
        Assert.Equal(1, retention.GetProperty("readyToRemove").GetInt32());
        var next = retention.GetProperty("nextLeaving");
        Assert.Equal("Leaving B", next[0].GetProperty("title").GetString());
        Assert.Equal("Leaving A", next[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Storage_is_read_from_the_background_reading_and_a_request_never_measures_a_disk()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await GetSummaryAsync(app);
        Assert.Equal(0, first.GetProperty("storage").GetProperty("volumes").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("storage").GetProperty("readingUtc").ValueKind);

        await app.Services.GetRequiredService<DashboardVolumeCache>().RefreshAsync(CancellationToken.None);
        Refresh(app);
        var second = await GetSummaryAsync(app);

        var volumes = second.GetProperty("storage").GetProperty("volumes");
        Assert.True(volumes.GetArrayLength() >= 1);
        Assert.Contains("data", volumes[0].GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.NotEqual(JsonValueKind.Null, second.GetProperty("storage").GetProperty("readingUtc").ValueKind);
    }

    [Fact]
    public async Task Simultaneous_callers_share_one_build_and_the_memo_answers_the_next_ones()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var service = (DashboardSummaryService)app.Services.GetRequiredService<IDashboardSummaryService>();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => app.Client.GetStringAsync("/api/dashboard/summary")));

        Assert.Equal(1, service.Builds);
        Assert.Single(responses.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("asOfUtc").GetString()).Distinct());

        await app.Client.GetStringAsync("/api/dashboard/summary");
        Assert.Equal(1, service.Builds);

        Refresh(app);
        await app.Client.GetStringAsync("/api/dashboard/summary");
        Assert.Equal(2, service.Builds);
    }

    /// <summary>#1097: the realtime hub pushes the changed sections themselves, built fresh and by name.</summary>
    [Fact]
    public async Task Named_sections_can_be_built_alone_for_a_push_and_drop_the_remembered_summary()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var source = app.Services.GetRequiredService<IDashboardSectionSource>();
        var service = (DashboardSummaryService)app.Services.GetRequiredService<IDashboardSummaryService>();
        await app.Client.GetStringAsync("/api/dashboard/summary");
        Assert.Equal(1, service.Builds);

        var built = await source.BuildSectionsAsync(["downloads", "health", "not-a-section"], CancellationToken.None);

        Assert.Equal(["downloads", "health"], built.Sections.Keys.Order().ToArray());
        Assert.Empty(built.Unavailable);
        Assert.IsType<DashboardDownloadsSection>(built.Sections["downloads"]);

        // What was just pushed must not be older than what the next fetch answers.
        await app.Client.GetStringAsync("/api/dashboard/summary");
        Assert.Equal(2, service.Builds);

        var everything = await source.BuildSectionsAsync(DashboardSectionNames.All, CancellationToken.None);
        Assert.Equal(SectionNames.Order().ToArray(), everything.Sections.Keys.Order().ToArray());
    }

    [Fact]
    public async Task The_summary_carries_the_quality_mix_collections_lists_added_and_recent_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedBigLibraryAsync(app, movies: 8, shows: 0, jobs: 0);
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await ExecuteAsync(databases, DelunoDatabaseNames.Movies,
            $"""
            INSERT INTO movie_collections (id, provider, provider_id, library_id, library_name, root_path, name, monitored, last_synced_utc, last_sync_error, created_utc, updated_utc)
            VALUES ('c1', 'tmdb', '10', 'lib', 'Films', '/data/films', 'Sample Collection', 1, '{now}', NULL, '{now}', '{now}');
            INSERT INTO movie_collection_members (collection_id, provider_id, title, is_excluded, local_movie_id, created_utc, updated_utc)
            VALUES ('c1', '1', 'One', 0, NULL, '{now}', '{now}'),
                   ('c1', '2', 'Two', 0, NULL, '{now}', '{now}'),
                   ('c1', '3', 'Three', 1, NULL, '{now}', '{now}');
            """);
        await ExecuteAsync(databases, DelunoDatabaseNames.Platform,
            $"""
            INSERT INTO intake_title_origins (id, source_id, source_name, provider, media_type, entity_id, entry_key, title, first_seen_utc, last_seen_utc)
            VALUES ('o1', 's1', 'A list', 'trakt', 'movies', 'e1', 'k1', 'Recent', '{now}', '{now}'),
                   ('o2', 's1', 'A list', 'trakt', 'movies', 'e2', 'k2', 'Old', '2020-01-01T00:00:00.0000000+00:00', '2020-01-01T00:00:00.0000000+00:00');
            """);
        await app.Services.GetRequiredService<Deluno.Jobs.Data.IActivityFeedRepository>()
            .RecordActivityAsync("download.vanished.recovered", "A download came back", null, null, null, null, CancellationToken.None);

        Refresh(app);
        var summary = await GetSummaryAsync(app);

        Assert.Equal(0, summary.GetProperty("unavailableSections").GetArrayLength());
        var library = summary.GetProperty("library");
        var quality = library.GetProperty("mix").GetProperty("movieQuality").EnumerateArray().ToArray();
        var tier = Assert.Single(quality);
        Assert.Equal("WEBDL-1080p", tier.GetProperty("label").GetString());
        Assert.Equal(4, tier.GetProperty("count").GetInt32());

        var collections = library.GetProperty("collections");
        Assert.Equal(1, collections.GetProperty("followed").GetInt32());
        Assert.Equal(1, collections.GetProperty("monitored").GetInt32());
        Assert.Equal(2, collections.GetProperty("members").GetInt32());
        Assert.Equal(0, collections.GetProperty("inLibrary").GetInt32());

        Assert.Equal(1, summary.GetProperty("requests").GetProperty("listTitlesAddedLast7Days").GetInt32());
        Assert.Equal(0, summary.GetProperty("subtitles").GetProperty("held").GetInt32());

        var recent = summary.GetProperty("activity").GetProperty("recent").EnumerateArray().ToArray();
        var item = Assert.Single(recent);
        Assert.Equal("download.vanished.recovered", item.GetProperty("category").GetString());
        Assert.Equal(Deluno.Contracts.ActivityEventLabels.For("download.vanished.recovered"), item.GetProperty("label").GetString());
        Assert.DoesNotContain(".", item.GetProperty("label").GetString()!);
        Assert.Equal("A download came back", item.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task A_big_library_answers_inside_the_budget_cold_and_warm()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryIds = await SeedBigLibraryAsync(app, movies: 3000, shows: 300, jobs: 10_000);

        // The first request pays for loading code and warming SQLite; it is not the measurement.
        await GetSummaryAsync(app);

        // The best of three of each, as ScreenEndpointsStayFastTests times the
        // screens: CI measured one fresh build at 1,007 ms and the memoised
        // answer after it at 879 ms, which is the runner stalling, not the build.
        // A build that is really slow is slow all three times.
        long cold = long.MaxValue, warm = long.MaxValue;
        JsonElement summary = default;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Refresh(app);
            var coldClock = Stopwatch.StartNew();
            summary = await GetSummaryAsync(app);
            cold = Math.Min(cold, coldClock.ElapsedMilliseconds);

            var warmClock = Stopwatch.StartNew();
            await GetSummaryAsync(app);
            warm = Math.Min(warm, warmClock.ElapsedMilliseconds);
        }

        Assert.Equal(0, summary.GetProperty("unavailableSections").GetArrayLength());
        var library = summary.GetProperty("library");
        Assert.Equal(3000, library.GetProperty("movies").GetProperty("total").GetInt32());
        Assert.Equal(1500, library.GetProperty("movies").GetProperty("monitored").GetInt32());
        Assert.Equal(300, library.GetProperty("shows").GetProperty("total").GetInt32());
        Assert.Equal(10_000, summary.GetProperty("system").GetProperty("jobs").GetProperty("queued").GetInt32());
        Assert.True(libraryIds.Length == 2);

        output.WriteLine($"best cold build {cold} ms (server {summary.GetProperty("buildMilliseconds").GetInt32()} ms), best memoized {warm} ms");
        Assert.True(cold < 150, $"cold build took {cold} ms at best of three");
        Assert.True(warm < 50, $"memoized answer took {warm} ms at best of three");
    }

    private static async Task<string[]> SeedBigLibraryAsync(ApplicationTestHost app, int movies, int shows, int jobs)
    {
        var libraries = app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>();
        var movieRepository = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var seriesRepository = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();

        var movieLibrary = await libraries.CreateLibraryAsync(NewLibrary("Films", "movies", "/data/films"), CancellationToken.None);
        var tvLibrary = await libraries.CreateLibraryAsync(NewLibrary("Shows", "tv", "/data/shows"), CancellationToken.None);

        string[] statuses = [WantedStatuses.Missing, WantedStatuses.Upgrade, WantedStatuses.Covered, WantedStatuses.Upcoming];
        for (var index = 0; index < movies; index++)
        {
            var status = statuses[index % statuses.Length];
            var movie = await movieRepository.AddAsync(
                new CreateMovieRequest($"Film {index:D5}", 1980 + (index % 40), null, index % 2 == 0, movieLibrary.Id),
                CancellationToken.None);
            var hasFile = status is WantedStatuses.Covered or WantedStatuses.Upgrade;
            await movieRepository.EnsureWantedStateAsync(
                movie.Id, movieLibrary.Id, status, "test-seed", hasFile,
                hasFile ? "WEBDL-1080p" : null, "WEBDL-1080p", status == WantedStatuses.Covered, CancellationToken.None);
        }

        for (var index = 0; index < shows; index++)
        {
            var show = await seriesRepository.AddAsync(
                new CreateSeriesRequest($"Show {index:D4}", 1990 + (index % 30), null, true),
                CancellationToken.None);
            await seriesRepository.EnsureWantedStateAsync(
                show.Id, tvLibrary.Id, WantedStatuses.Missing, "test-seed", false, null, "WEBDL-1080p", false, CancellationToken.None);
        }

        await using (var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO job_queue (id, job_type, source, status, attempts, created_utc, scheduled_utc)
                VALUES (@id, 'library.search.movies', 'test', 'queued', 0, '2026-09-30T00:00:00.0000000+00:00', '2026-09-30T00:00:00.0000000+00:00');
                """;
            var id = command.CreateParameter();
            id.ParameterName = "@id";
            command.Parameters.Add(id);
            for (var index = 0; index < jobs; index++)
            {
                id.Value = string.Create(CultureInfo.InvariantCulture, $"seed-job-{index}");
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        return [movieLibrary.Id, tvLibrary.Id];
    }

    private static Deluno.Libraries.Contracts.CreateLibraryRequest NewLibrary(string name, string mediaType, string root)
        => new(
            Name: name,
            MediaType: mediaType,
            Purpose: "General",
            RootPath: root,
            DownloadsPath: null,
            QualityProfileId: null,
            ImportWorkflow: null,
            ProcessorName: null,
            ProcessorOutputPath: null,
            ProcessorTimeoutMinutes: null,
            ProcessorFailureMode: null,
            AutoSearchEnabled: false,
            MissingSearchEnabled: false,
            UpgradeSearchEnabled: false,
            SearchIntervalMinutes: null,
            RetryDelayHours: null,
            MaxItemsPerRun: null);

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory databases, string database, string sql)
    {
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ExplodingHealthRepository : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheckTransitionResult>> UpsertResultsAsync(IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheckTransitionResult>>([]);

        public Task<IReadOnlyList<HealthCheckResultItem>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("The health store is down.");

        public Task<HealthCheckResultItem?> GetAsync(string id, CancellationToken cancellationToken) => throw new InvalidOperationException("The health store is down.");

        public Task<HealthCheckSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("The health store is down.");

        public Task<HealthCheckResultItem?> SnoozeAsync(string id, DateTimeOffset? untilUtc, CancellationToken cancellationToken) => Task.FromResult<HealthCheckResultItem?>(null);

        public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RequestImmediateRunAsync(string category, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> TakePendingImmediateRunsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class CountingConnectivityMonitor : IConnectivityMonitor
    {
        public int Probes;

        public ConnectivityStatus Current { get; } = new(false, null, DateTimeOffset.UtcNow, [], "Connected.");

        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(Current);
        }

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(TimeSpan.Zero);
        }
    }

    /// <summary>Stands in for a whole interface, counts every call, and fails each one.</summary>
    public class CallCountingProxy : DispatchProxy
    {
        public int Calls;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException($"The Dashboard must not call {targetMethod?.Name}.");
        }
    }
}
