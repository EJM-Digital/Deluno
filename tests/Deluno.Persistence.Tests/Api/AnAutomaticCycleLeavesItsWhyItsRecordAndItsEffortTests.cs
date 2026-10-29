using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Automatic upgrade-hunting (#639), once per kind of title: a library whose cycle is due, holding a film or an episode on disk below
/// what its profile wants, finds a better release by itself and sends it to its client, replacing nothing until the new file is in.
/// Jackett 0.24.2850 and qBittorrent 5.2.1 are the recorded apps over real loopback ports; the library cycle is the real planner and
/// the real library search handler on the real databases, run the way the worker's lane runs them.
/// </summary>
public sealed class AnAutomaticCycleLeavesItsWhyItsRecordAndItsEffortTests
{
    private const string FilmPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string ShowPath = "/api/v2.0/indexers/thepiratebay/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private sealed class Rig(RecordedAppServer jackett, RecordedAppServer qbit, ApplicationTestHost app, string clientId, string filmIndexerId, string showIndexerId) : IAsyncDisposable
    {
        public RecordedAppServer Jackett { get; } = jackett;

        public RecordedAppServer Qbit { get; } = qbit;

        public ApplicationTestHost App { get; } = app;

        public string ClientId { get; } = clientId;

        public string FilmIndexerId { get; } = filmIndexerId;

        public string ShowIndexerId { get; } = showIndexerId;

        public int Adds => Qbit.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Qbit.DisposeAsync();
            await Jackett.DisposeAsync();
        }
    }

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Rig> RigAsync()
    {
        var jackett = await RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-movie-imdb", "search-tv", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.Chain(
                (request, _) => request.RequestUri is { } uri && uri.Query.Contains("t=search&q=", StringComparison.Ordinal)
                    ? (uri.AbsolutePath.Contains("/yts/", StringComparison.Ordinal) ? RecordedReplies.Jackett("search-text").Exchanges[0] : RecordedReplies.Jackett("search-tv").Exchanges[1]).Reply.ToResponse()
                    : null,
                RecordedAppServer.ByQuery(
                    "t",
                    ("caps", RecordedReplies.Jackett("caps").Exchanges[0]),
                    ("movie", RecordedReplies.Jackett("search-movie-imdb").Exchanges[0]),
                    ("tvsearch", RecordedReplies.Jackett("search-tv").Exchanges[0]),
                    ("search", RecordedReplies.Jackett("search-movie-imdb").Exchanges[1]))));
        var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "category-moved", "auto-management-set" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0]));
        var app = await ApplicationTestHost.StartAsync();

        async Task<string> IndexerAsync(string path, string scope, string categories)
        {
            var created = await PostOkAsync(app, "/api/indexers", new
            {
                protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + path, apiKey = Key, priority = 25,
                categories, tags = "", mediaScope = scope, isEnabled = true, preferMagnetUrl = true
            });
            var id = created.GetProperty("id").GetString()!;
            Assert.Equal("healthy", (await PostOkAsync(app, $"/api/indexers/{id}/test")).GetProperty("healthStatus").GetString());
            return id;
        }

        var client = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = client.GetProperty("id").GetString()!;
        Assert.Equal("healthy", (await (await app.Client.PostAsync($"/api/download-clients/{clientId}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        return new Rig(jackett, qbit, app, clientId, await IndexerAsync(FilmPath, "movies", "2000"), await IndexerAsync(ShowPath, "tv", "5000"));
    }

    private static async Task<string> RoutedLibraryAsync(Rig rig, string mediaType, string name, string indexerId)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(rig.App, mediaType, name, name);
        var routed = await rig.App.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId, priority = 1, requiredTags = "", excludedTags = "" } },
            downloadClients = new[] { new { downloadClientId = rig.ClientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return libraryId;
    }

    /// <summary>The cycle of the real planner and the real handler: the library is due, its search job is queued and then run.</summary>
    private static async Task RunTheCycleAsync(Rig rig, string libraryId, string name, string mediaType, bool missing = false, bool upgrades = true, bool manual = true)
    {
        var jobs = rig.App.Services.GetRequiredService<IJobQueueRepository>();
        var due = new LibraryAutomationPlanItem(libraryId, name, mediaType, true, missing, upgrades, 1, 24, 25, null, null);
        if (manual) Assert.True(await jobs.RequestLibrarySearchAsync(due, CancellationToken.None));
        await jobs.PlanLibrarySearchesAsync([due], CancellationToken.None);
        var queued = Assert.Single(await jobs.ListAsync(100, CancellationToken.None), item => Deluno.Contracts.LibrarySearchJobTypes.IsLibrarySearch(item.JobType));
        var registry = new JobHandlerRegistry(rig.App.Services.GetServices<IJobHandler>());
        await registry.Resolve(queued.JobType).HandleAsync(queued, CancellationToken.None);
        // The cycle hands a title's own search to the next job in the lane (an episode's search); the lane runs those next.
        foreach (var next in (await jobs.ListAsync(100, CancellationToken.None)).Where(item => item.Id != queued.Id && item.Status == "queued" && item.JobType.Contains("search", StringComparison.Ordinal)).ToArray())
        {
            await registry.Resolve(next.JobType).HandleAsync(next, CancellationToken.None);
        }
    }

    private static async Task<(string MovieId, string LibraryId)> MissingFilmAsync(Rig rig)
    {
        var films = await RoutedLibraryAsync(rig, "movies", "Films", rig.FilmIndexerId);
        var added = await PostOkAsync(rig.App, "/api/movies/", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = films, searchForMissingNow = false });
        var movieId = added.GetProperty("id").GetString()!;
        var profile = await ProfileAsync(rig);
        (await rig.App.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { movieId }, qualityProfileId = profile })).EnsureSuccessStatusCode();
        return (movieId, films);
    }

    private static async Task<string> ProfileAsync(Rig rig)
        => (await PostOkAsync(rig.App, "/api/quality-profiles", new
        {
            name = "Small 720p films, upgrading", mediaType = "movies", cutoffQuality = "Bluray 720p", allowedQualities = "DVD,Bluray 720p", customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        })).GetProperty("id").GetString()!;

    private static async Task<JsonElement[]> ActivityAsync(Rig rig)
        => (await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/activity?pageSize=100")).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task A_scheduled_missing_cycle_writes_one_cycle_record_its_two_Activity_lines_and_the_dispatch_says_why_it_ran()
    {
        await using var rig = await RigAsync();
        var (movieId, films) = await MissingFilmAsync(rig);

        await RunTheCycleAsync(rig, films, "Films", "movies", missing: true, upgrades: false, manual: false);

        var cycle = Assert.Single((await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/search-cycles")).GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("schedule", cycle.GetProperty("triggerKind").GetString());
        Assert.Equal("completed", cycle.GetProperty("status").GetString());
        Assert.Equal("missing", cycle.GetProperty("searchKind").GetString());
        Assert.Equal(1, cycle.GetProperty("plannedCount").GetInt32());
        Assert.Equal(1, cycle.GetProperty("queuedCount").GetInt32());
        var notes = JsonDocument.Parse(cycle.GetProperty("notesJson").GetString()!).RootElement;
        Assert.Equal(1, notes.GetProperty("configuredSources").GetInt32());
        Assert.Equal(1, notes.GetProperty("configuredClients").GetInt32());
        Assert.Equal(1, notes.GetProperty("matchedCount").GetInt32());
        Assert.True(notes.GetProperty("apiCallCount").GetInt32() >= 1);
        Assert.True(notes.GetProperty("queuedReleaseBytes").GetInt64() > 0);

        var activity = await ActivityAsync(rig);
        Assert.Equal("Checked Films: 1 planned, 1 sent, 0 waiting.", Assert.Single(activity, item => item.GetProperty("category").GetString() == "library.search.cycle").GetProperty("message").GetString());
        Assert.Contains("Deluno checked 1 movie in Films", Assert.Single(activity, item => item.GetProperty("category").GetString() == "library.search.executed").GetProperty("message").GetString());

        Assert.Equal("missing", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{movieId}';"));
    }

    [Fact]
    public async Task A_manual_request_is_recorded_as_manual_on_the_cycle_and_the_search_is_counted_against_the_title()
    {
        await using var rig = await RigAsync();
        var (movieId, films) = await MissingFilmAsync(rig);
        Assert.Equal(0, await ApiScenes.ScalarAsync(rig.App, "movies", $"SELECT COUNT(*) FROM movie_search_history WHERE movie_id = '{movieId}';"));

        await RunTheCycleAsync(rig, films, "Films", "movies", missing: true, upgrades: false);

        var cycle = Assert.Single((await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/search-cycles")).GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("manual", cycle.GetProperty("triggerKind").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "movies", $"SELECT COALESCE(search_attempt_count, 0) FROM movie_entries WHERE id = '{movieId}';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "movies", $"SELECT COALESCE(search_grab_count, 0) FROM movie_entries WHERE id = '{movieId}';"));
        var grab = Assert.Single(await ActivityAsync(rig), item => item.GetProperty("category").GetString() == "movie.release.grabbed");
        Assert.Contains("Sintel (2010) 720p BRRip x264 -YTS", grab.GetProperty("message").GetString());
        Assert.Equal("grabbed", grab.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_upgrade_cycle_marks_its_dispatch_as_an_upgrade()
    {
        await using var rig = await RigAsync();
        var films = await RoutedLibraryAsync(rig, "movies", "Films", rig.FilmIndexerId);
        var profile = await ProfileAsync(rig);
        var held = await ApiScenes.FilmOnDiskAsync(rig.App, "Sintel", 2010, films, quality: "DVD");
        (await rig.App.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { held.MovieId }, qualityProfileId = profile })).EnsureSuccessStatusCode();

        await RunTheCycleAsync(rig, films, "Films", "movies", manual: false);

        Assert.Equal("upgrade", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{held.MovieId}';"));
        Assert.Equal("upgrade", Assert.Single((await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/search-cycles")).GetProperty("items").EnumerateArray().ToArray()).GetProperty("searchKind").GetString());
    }
}
