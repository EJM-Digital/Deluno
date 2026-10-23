using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The read routes an outside tool polls under /api/integrations/external (coverage register, engines): the manifest names the
/// product, the capabilities it can rely on, the categories to use and every library, indexer, download client and connection as
/// stored; health counts what is enabled and what is not healthy; queue and activity take a bounded number and queue filters its
/// dispatches by the names the documents use. Refs #1296.
/// </summary>
public sealed class TheExternalReadRoutesAnswerWhatTheyPromiseTests
{
    private const string External = "/api/integrations/external";

    private static async Task<string> AddIndexerAsync(ApplicationTestHost app, string name, string health, bool enabled = true)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", new CreateIndexerRequest(name, "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", enabled)
        {
            TestedHealth = new IndexerTestedHealth(health, null, null, 10)
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> AddClientAsync(ApplicationTestHost app, string name, bool enabled = true)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name, protocol = "qbittorrent", host = "127.0.0.1", port = 9, username = "u", password = "p-test-password",
            moviesCategory = "movies", tvCategory = (string?)null, priority = 5, isEnabled = enabled
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_manifest_names_the_product_the_capabilities_the_categories_and_everything_the_install_holds()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var indexerId = await AddIndexerAsync(app, "Alpha", "healthy");
        var clientId = await AddClientAsync(app, "Qbit");

        var manifest = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/manifest");

        Assert.Equal("Deluno", manifest.GetProperty("product").GetString());
        Assert.Equal(Deluno.Contracts.DelunoApiVersion.Current, manifest.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("instanceName").GetString()));
        var capabilities = manifest.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        foreach (var documented in new[] { "movies", "tv", "indexers", "download-clients", "library-routing", "destination-rules", "metadata", "media-probing", "external-file-changed", "external-redownload", "pre-import-processing", "activity-feed", "signalr" })
        {
            Assert.Contains(documented, capabilities);
        }

        var categories = manifest.GetProperty("recommendedCategories");
        Assert.Equal("deluno-movies", categories.GetProperty("movies").GetString());
        Assert.Equal("deluno-tv", categories.GetProperty("tv").GetString());
        var library = Assert.Single(manifest.GetProperty("libraries").EnumerateArray());
        Assert.Equal(libraryId, library.GetProperty("id").GetString());
        Assert.Equal("movies", library.GetProperty("mediaType").GetString());
        Assert.Equal(await ApiScenes.LibraryRootAsync(app, libraryId), library.GetProperty("rootPath").GetString());
        var indexer = Assert.Single(manifest.GetProperty("indexers").EnumerateArray());
        Assert.Equal(indexerId, indexer.GetProperty("id").GetString());
        Assert.Equal("Alpha", indexer.GetProperty("name").GetString());
        Assert.Equal("healthy", indexer.GetProperty("healthStatus").GetString());
        Assert.True(indexer.GetProperty("isEnabled").GetBoolean());
        var client = Assert.Single(manifest.GetProperty("downloadClients").EnumerateArray());
        Assert.Equal(clientId, client.GetProperty("id").GetString());
        Assert.Equal("movies", client.GetProperty("moviesCategory").GetString());
        Assert.Equal("deluno-tv", client.GetProperty("tvCategory").GetString());
        Assert.Equal(JsonValueKind.Array, manifest.GetProperty("connections").ValueKind);
    }

    [Fact]
    public async Task Health_counts_the_libraries_and_what_is_enabled_and_calls_an_enabled_connection_that_is_not_healthy_a_problem()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        await AddIndexerAsync(app, "Good", "healthy");
        await AddIndexerAsync(app, "Broken", "degraded");
        await AddIndexerAsync(app, "Off", "degraded", enabled: false);
        await AddClientAsync(app, "Untested");
        await AddClientAsync(app, "Off client", enabled: false);

        var health = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/health");

        Assert.Equal("online", health.GetProperty("status").GetString());
        Assert.Equal(1, health.GetProperty("libraryCount").GetInt32());
        Assert.Equal(2, health.GetProperty("enabledIndexerCount").GetInt32());
        Assert.Equal(1, health.GetProperty("enabledDownloadClientCount").GetInt32());
        Assert.Equal(0, health.GetProperty("activeJobCount").GetInt32());
        // The broken indexer and the untested client count; the two that are switched off do not, and the healthy one does not.
        Assert.Equal(2, health.GetProperty("problemCount").GetInt32());
        Assert.True(health.GetProperty("checkedUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task The_queue_and_the_activity_take_a_number_that_is_kept_inside_their_limits_and_the_queue_lists_its_dispatches_by_either_name_of_a_kind()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            await ApiScenes.ExecuteAsync(app, "jobs", $"INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc) VALUES ('d{i}', 'lib', 'movies', 'movie', 'm{i}', 'Film.{i}.1080p', 'ix', 'c', 'C', 'initial', datetime('now'));");
        }

        await ApiScenes.ExecuteAsync(app, "jobs", "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc) VALUES ('t0', 'lib', 'tv', 'series', 's0', 'Show.S01E01', 'ix', 'c', 'C', 'initial', datetime('now'));");
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        for (var i = 0; i < 4; i++)
        {
            await feed.RecordActivityAsync("test.entry", $"Entry {i}", null, null, null, null, CancellationToken.None);
        }

        var films = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/queue?mediaType=movie");
        var shows = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/queue?mediaType=series");
        var everything = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/queue?take=2");
        var activity = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/activity?take=2");
        var tooMany = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/activity?take=100000");
        var none = await app.Client.GetFromJsonAsync<JsonElement>($"{External}/activity?take=0");

        Assert.Equal(["d0", "d1", "d2"], films.GetProperty("dispatches").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).Order().ToArray());
        Assert.Equal(["t0"], shows.GetProperty("dispatches").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(2, everything.GetProperty("dispatches").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, everything.GetProperty("jobs").ValueKind);
        Assert.Equal(2, activity.GetArrayLength());
        Assert.True(tooMany.GetArrayLength() >= 4);
        Assert.Equal(1, none.GetArrayLength());
    }

    [Fact]
    public async Task Asking_for_a_refresh_writes_one_Activity_line_saying_how_many_libraries_and_why()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.CreateLibraryAsync(app, "movies");
        await ApiScenes.CreateLibraryAsync(app, "tv");

        var response = await app.Client.PostAsJsonAsync($"{External}/trigger-refresh", new { mediaType = "movies", reason = "Home Assistant button" });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enqueued").GetInt32());
        Assert.Equal("An external app requested refresh for 1 library.", await ApiScenes.TextAsync(app, "jobs", "SELECT message FROM activity_events WHERE category = 'integration';"));
        var details = JsonDocument.Parse((await ApiScenes.TextAsync(app, "jobs", "SELECT details_json FROM activity_events WHERE category = 'integration';"))!).RootElement;
        Assert.Equal("Home Assistant button", details.GetProperty("Reason").GetString());
        Assert.Equal(film, Assert.Single(details.GetProperty("libraries").EnumerateArray()).GetString());
    }
}
