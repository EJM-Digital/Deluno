using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The clear-out, import and outside-app routes when the request is right (coverage register, area api): what each
/// changes in the real databases and on the real disk, and what it says in Activity.
/// </summary>
public sealed class MaintenanceRoutesDoWhatTheySayTests
{
    /// <summary>What the host's real SQLite file holds, read straight out of it so a test sees what is stored and not only what is answered.</summary>
    private static async Task<string?> StoredTextAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = (Microsoft.Data.Sqlite.SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Clearing_Activity_removes_every_entry_and_leaves_one_line_saying_it_was_cleared()
    {
        await using var app = await StartAsync();
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        for (var i = 0; i < 4; i++)
        {
            await feed.RecordActivityAsync("test.entry", $"Entry number {i}", null, null, null, null, CancellationToken.None);
        }

        var before = await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events;");
        Assert.True(before >= 4);

        var result = await PostOkAsync(app, "/api/activity/clear");

        Assert.Equal(before, result.GetProperty("removed").GetInt64());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events;"));
        var text = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains($"Activity history was cleared ({before} entries removed).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Entry number 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_strikes_clears_every_recorded_strike_and_says_how_many_downloads_were_reset()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", "movie-1", "Stuck.Film.2020.1080p-DELUNO", "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: "item-1");
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        await health.RecordDownloadHealthObservationsAsync(
            "client-1", ["item-1"],
            [new DownloadHealthObservation("client-1", "item-1", "Stuck.Film.2020.1080p-DELUNO", DownloadHealthStrikes.ClientStalled, "warning", "No throughput for 30 minutes.")],
            DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None);
        Assert.Single(await health.ListDownloadHealthRecordsAsync(100, CancellationToken.None));
        Assert.Contains("Stuck.Film.2020", await StoredTextAsync(app, "platform", "SELECT setting_value FROM system_settings WHERE setting_key = 'download-health.records.v2';"), StringComparison.Ordinal);

        var result = await PostOkAsync(app, "/api/download-health/purge-strikes");
        Assert.Equal("[]", await StoredTextAsync(app, "platform", "SELECT setting_value FROM system_settings WHERE setting_key = 'download-health.records.v2';"));

        Assert.Equal(1, result.GetProperty("purgedCount").GetInt32());
        Assert.Empty(await health.ListDownloadHealthRecordsAsync(100, CancellationToken.None));
        Assert.Contains("1 download reset to zero", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

        var again = await PostOkAsync(app, "/api/download-health/purge-strikes");
        Assert.Equal(0, again.GetProperty("purgedCount").GetInt32());
        Assert.Contains("There was nothing to clear.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outside_apps_refresh_request_asks_for_a_search_in_the_libraries_of_that_kind_only_and_says_so_in_Activity()
    {
        await using var app = await StartAsync();
        var films = await ApiScenes.CreateLibraryAsync(app, "movies");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");

        var result = await PostOkAsync(app, "/api/integrations/external/trigger-refresh", new { mediaType = "movies", reason = "Weir finished a batch" });

        Assert.Equal(1, result.GetProperty("enqueued").GetInt32());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT search_requested FROM library_automation_state WHERE library_id = '{films}';"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT search_requested FROM library_automation_state WHERE library_id = '{shows}';"));
        Assert.Contains("An external app requested refresh for 1 library.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

        var all = await PostOkAsync(app, "/api/integrations/external/trigger-refresh", new { });
        Assert.Equal(2, all.GetProperty("enqueued").GetInt32());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT search_requested FROM library_automation_state WHERE library_id = '{shows}';"));
    }

    [Fact]
    public async Task An_outside_apps_redownload_request_moves_the_file_to_the_recycle_bin_marks_the_film_missing_and_queues_one_search()
    {
        await using var app = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);
        Assert.True(File.Exists(film.FilePath));

        var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = film.FilePath, reason = "Keep Japanese audio", tool = "Weir" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(film.MovieId, body.GetProperty("titleId").GetString());
        Assert.True(body.GetProperty("fileRemoved").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("searchJobId").GetString()));
        Assert.Contains("Weir asked Deluno to download Dune again: Keep Japanese audio.", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(film.FilePath), "the file left the library");
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength() > 0, "it is held in the recycle bin");
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}");
        Assert.True(detail.GetProperty("monitored").GetBoolean(), "the title stays wanted so the search can fetch it");
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE id = '{body.GetProperty("searchJobId").GetString()}' AND source = 'external-redownload';"));

        var unknown = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = Path.Combine(app.DataRoot, "not", "tracked.mkv") });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var blank = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Give the full path of the library file", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleaning_up_the_recycle_bin_deletes_the_expired_entries_for_good_and_keeps_the_rest()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var old = await ApiScenes.FilmOnDiskAsync(app, "Old Film", 1999, libraryId);
        var fresh = await ApiScenes.FilmOnDiskAsync(app, "Fresh Film", 2020, libraryId);
        foreach (var film in new[] { old, fresh })
        {
            var deleted = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files")
            {
                Content = JsonContent.Create(new { path = film.FilePath })
            });
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        var items = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");
        Assert.Equal(2, items.GetArrayLength());
        var oldItem = items.EnumerateArray().Single(item => item.GetProperty("originalPath").GetString()!.Contains("Old Film", StringComparison.Ordinal));
        var freshItem = items.EnumerateArray().Single(item => item.GetProperty("originalPath").GetString()!.Contains("Fresh Film", StringComparison.Ordinal));
        var oldPath = oldItem.GetProperty("recyclePath").GetString()!;
        var freshPath = freshItem.GetProperty("recyclePath").GetString()!;
        Assert.True(File.Exists(oldPath) && File.Exists(freshPath));

        // Age the old one past its retention, the way the passing of time would.
        var statePath = Path.Combine(app.DataRoot, "recycle-bin", "state.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        foreach (var item in state["items"]!.AsArray())
        {
            if (item!["originalPath"]!.GetValue<string>().Contains("Old Film", StringComparison.Ordinal))
            {
                item["expiresUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
            }
        }

        await File.WriteAllTextAsync(statePath, state.ToJsonString());

        var preview = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/cleanup/preview");
        Assert.Contains("1", preview.GetRawText(), StringComparison.Ordinal);
        var result = await PostOkAsync(app, "/api/recycle-bin/cleanup");

        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        Assert.False(File.Exists(oldPath) || Directory.Exists(oldPath), "the expired entry is deleted for good");
        Assert.True(File.Exists(freshPath), "an entry still inside its retention is kept");
        var left = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");
        Assert.Equal(freshItem.GetProperty("id").GetString(), Assert.Single(left.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Starting_the_existing_media_import_begins_a_run_for_the_library_queues_its_first_slice_and_says_so()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var file = Path.Combine(root, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        var response = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var progress = await response.Content.ReadFromJsonAsync<JsonElement>();
        var run = progress.GetProperty("run");
        Assert.Equal(libraryId, run.GetProperty("libraryId").GetString());
        Assert.Contains(run.GetProperty("status").GetString(), new[] { "queued", "running" });
        Assert.Contains("Deluno started bringing in what is already in Films.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
        Assert.True(await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE '%import%existing%' OR job_type LIKE '%existing%import%';") >= 1, "the first slice of work is queued");
        Assert.True(File.Exists(file), "starting an import never moves the files it will read");
        var read = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing");
        Assert.Equal(libraryId, read.GetProperty("run").GetProperty("libraryId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/libraries/no-such-library/import-existing", content: null)).StatusCode);
    }

    [Fact]
    public async Task Pausing_and_resuming_the_existing_media_import_changes_the_run_state_and_resuming_queues_more_work()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var file = Path.Combine(root, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);

        var paused = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null);
        Assert.Equal(HttpStatusCode.Accepted, paused.StatusCode);
        Assert.Equal("paused", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("paused", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing")).GetProperty("run").GetProperty("status").GetString());

        var jobsBefore = await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue;");
        var resumed = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/resume", content: null);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal("running", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("status").GetString());
        Assert.True(await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue;") >= jobsBefore, "resuming hands the next slice to the worker");
        Assert.True(File.Exists(file));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/libraries/no-such-library/import-existing/pause", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/libraries/no-such-library/import-existing/resume", content: null)).StatusCode);
    }

    [Fact]
    public async Task Importing_chosen_existing_files_brings_each_into_the_catalogue_with_its_file_and_says_what_it_did()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var dune = Path.Combine(root, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        var arrival = Path.Combine(root, "Arrival (2016)", "Arrival (2016) WEB-1080p.mkv");
        foreach (var path in new[] { dune, arrival })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        }

        var result = await PostOkAsync(app, $"/api/libraries/{libraryId}/import-existing/selected", new { sourcePaths = new[] { dune } });

        Assert.Equal(1, result.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("importedCount").GetInt32());
        var titles = (await app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Select(item => item.Title).ToArray();
        Assert.Equal("Dune", Assert.Single(titles));
        Assert.True(File.Exists(dune) && File.Exists(arrival), "importing existing files records them where they are; nothing moves");
        Assert.Contains("library.import.existing.selected", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

        var none = await app.Client.PostAsJsonAsync($"/api/libraries/{libraryId}/import-existing/selected", new { sourcePaths = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Select at least one file or folder to import.", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_a_radarr_export_creates_its_profiles_libraries_indexers_and_clients_and_a_second_apply_adds_nothing_twice()
    {
        await using var app = await StartAsync();
        var payload = """
            {
              "qualityProfiles": [ { "id": 80, "name": "Migrated UHD", "cutoff": 3, "items": [
                { "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } },
                { "allowed": true, "quality": { "id": 3, "name": "Remux 2160p" } } ] } ],
              "rootFolders": [ { "path": "/mnt/media/migrated-movies" } ],
              "indexers": [ { "name": "Migrated Torrent", "protocol": "torrent", "baseUrl": "https://indexer.example/api", "apiKey": "secret", "categories": [2000, 2010], "enable": true } ],
              "downloadClients": [ { "name": "Migrated qBittorrent", "implementation": "QBittorrent", "host": "qbittorrent", "port": 8080,
                "fields": [ { "name": "category", "value": "movies" }, { "name": "username", "value": "deluno" }, { "name": "password", "value": "secret" } ], "enable": true } ],
              "movies": [ { "title": "Anora", "monitored": false, "hasFile": false } ]
            }
            """;
        var request = new { sourceKind = "radarr", sourceName = "Radarr test", payloadJson = payload };
        var profilesBefore = await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM quality_profiles;");

        var applied = await PostOkAsync(app, "/api/migration/apply", request);

        Assert.True(applied.GetProperty("report").GetProperty("valid").GetBoolean());
        Assert.Equal(profilesBefore + 1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM quality_profiles;"));
        var profiles = await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles");
        Assert.Contains(profiles.EnumerateArray(), profile => profile.GetProperty("name").GetString() == "Migrated UHD");
        var indexers = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers");
        Assert.Contains(indexers.EnumerateArray(), item => item.GetProperty("name").GetString() == "Migrated Torrent");
        var clients = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-clients");
        Assert.Contains(clients.EnumerateArray(), item => item.GetProperty("host").GetString() == "qbittorrent" && item.GetProperty("protocol").GetString() == "qbittorrent");
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries");
        Assert.Contains(libraries.EnumerateArray(), item => item.GetProperty("rootPath").GetString() == "/mnt/media/migrated-movies");

        await PostOkAsync(app, "/api/migration/apply", request);
        Assert.Equal(profilesBefore + 1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM quality_profiles;"));
        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers")).EnumerateArray().Count(item => item.GetProperty("name").GetString() == "Migrated Torrent"));

        var invalid = await app.Client.PostAsJsonAsync("/api/migration/apply", new { sourceKind = "radarr", sourceName = "Broken", payloadJson = "{ not json" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
