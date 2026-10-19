using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// A saved filter set to "Search on cycle" (coverage register, area library): the whole worker runs the library's scheduled search
/// cycle, and only the titles that filter selects are searched; with the same filter left as "View only" the cycle searches every
/// wanted title. Jackett 0.24.2850 is the recorded indexer and qBittorrent 5.2.1 the recorded client, over real loopback ports;
/// the heartbeat worker, the job queue, the saved view, the planner, the search handler and the movie database are the real ones.
/// What each title was searched for is read from its own search history on disk.
/// </summary>
public sealed class ASavedViewScopesTheLibrarySearchCycleTests
{
    private const string FilmPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private sealed class SwitchAutomationOn(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(true, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Scene(string BigBuckBunny, string ElephantsDream, string LibraryId);

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<RecordedAppServer> JackettAsync()
        => await RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-movie-imdb", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.ByQuery("t", ("caps", RecordedReplies.Jackett("caps").Exchanges[0]), ("movie", RecordedReplies.Jackett("search-movie-imdb").Exchanges[0]), ("search", RecordedReplies.Jackett("search-text").Exchanges[0])));

    private static async Task<RecordedAppServer> QbitAsync()
        => await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings", "resumed" }.Select(RecordedReplies.Qbittorrent));

    private static async Task<ApplicationTestHost> HostAsync()
        => await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddHostedService<SwitchAutomationOn>();
            // The internet is up: the planner skips searching during an outage, and a test machine may have none.
            services.RemoveAll<IConnectivityMonitor>();
            services.AddSingleton<IConnectivityMonitor>(NullConnectivityMonitor.Instance);
            services.AddHostedService<DelunoHeartbeatWorker>();
        });

    /// <summary>One library, one indexer, one client, two wanted films (2008 and 2006) and a saved filter for films from 2007 on.</summary>
    private static async Task<Scene> SceneAsync(ApplicationTestHost app, RecordedAppServer jackett, RecordedAppServer qbit, string? automationAction)
    {
        var client = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = client.GetProperty("id").GetString()!;
        var indexer = await PostOkAsync(app, "/api/indexers", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + FilmPath, apiKey = Key, priority = 25,
            categories = "2000", tags = "", mediaScope = "movies", isEnabled = true, preferMagnetUrl = true
        });
        var indexerId = indexer.GetProperty("id").GetString()!;
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await PostOkAsync(app, "/api/libraries/", new
        {
            name = "Films", mediaType = "movies", purpose = "collection", rootPath = root, importWorkflow = "copy",
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        var libraryId = library.GetProperty("id").GetString()!;
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId, priority = 1, requiredTags = string.Empty, excludedTags = string.Empty } },
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var bunny = (await PostOkAsync(app, "/api/movies", new { title = "Big Buck Bunny", releaseYear = 2008, imdbId = "tt1254207", monitored = true, libraryId, searchForMissingNow = false })).GetProperty("id").GetString()!;
        var dream = (await PostOkAsync(app, "/api/movies", new { title = "Elephants Dream", releaseYear = 2006, imdbId = "tt0807840", monitored = true, libraryId, searchForMissingNow = false })).GetProperty("id").GetString()!;
        // No metadata provider answers in a test, so neither film has a release date; "announced" lets a cycle look for a film anyway.
        await PostOkAsync(app, "/api/movies/bulk/minimum-availability", new { movieIds = new[] { bunny, dream }, minimumAvailability = "announced" });
        await PostOkAsync(app, "/api/library-views/", new
        {
            variant = "movies", libraryId, name = "Recent films", quickFilter = "missing", monitoring = "monitored", sortField = "title", sortDirection = "asc",
            viewMode = "grid", cardSize = "md", displayOptionsJson = "{}",
            rulesJson = JsonSerializer.Serialize(new[] { new { field = "year", @operator = "min", values = new[] { "2007" } } }),
            automationAction
        });

        // Last of all, so the first planning pass already sees the films and the saved filter.
        var on = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/automation", new
        {
            autoSearchEnabled = true, missingSearchEnabled = true, upgradeSearchEnabled = false, searchIntervalMinutes = 60, retryDelayHours = 24, maxItemsPerRun = 25
        });
        Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync());
        return new Scene(bunny, dream, libraryId);
    }

    private static async Task<long> SearchesOfAsync(ApplicationTestHost app, string movieId)
        => await ApiScenes.ScalarAsync(app, "movies", $"SELECT COUNT(*) FROM movie_search_history WHERE movie_id = '{movieId}' AND trigger_kind = 'schedule';");

    [Fact]
    public async Task With_Search_on_cycle_only_the_films_the_saved_filter_selects_are_searched()
    {
        await using var jackett = await JackettAsync();
        await using var qbit = await QbitAsync();
        await using var app = await HostAsync();
        var scene = await SceneAsync(app, jackett, qbit, automationAction: "search");

        await Wait.UntilAsync(async () => await SearchesOfAsync(app, scene.BigBuckBunny) > 0, "the scheduled cycle to search the film the filter selects", TimeSpan.FromSeconds(90));
        // The cycle is one job; once it has searched the selected film it has finished with the rest.
        await Wait.UntilAsync(
            async () => await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE 'library.search%' AND source = 'movies' AND status IN ('queued', 'running');") == 0,
            "the cycle's search job to finish",
            TimeSpan.FromSeconds(60));

        Assert.Equal(0, await SearchesOfAsync(app, scene.ElephantsDream));
        Assert.Contains("scopeId", await ApiScenes.TextAsync(app, "jobs", "SELECT payload_json FROM job_queue WHERE job_type LIKE 'library.search%' AND source = 'movies' ORDER BY created_utc DESC LIMIT 1;") ?? string.Empty);
    }

    [Fact]
    public async Task With_the_same_filter_left_as_View_only_the_cycle_searches_every_wanted_film()
    {
        await using var jackett = await JackettAsync();
        await using var qbit = await QbitAsync();
        await using var app = await HostAsync();
        var scene = await SceneAsync(app, jackett, qbit, automationAction: null);

        await Wait.UntilAsync(
            async () => await SearchesOfAsync(app, scene.BigBuckBunny) > 0 && await SearchesOfAsync(app, scene.ElephantsDream) > 0,
            "the scheduled cycle to search both films",
            TimeSpan.FromSeconds(90));
    }
}
