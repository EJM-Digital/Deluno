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
public sealed class AnAutomaticCycleHuntsAnUpgradeForEachKindOfTitleTests
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
    private static async Task RunTheCycleAsync(Rig rig, string libraryId, string name, string mediaType)
    {
        var jobs = rig.App.Services.GetRequiredService<IJobQueueRepository>();
        var due = new LibraryAutomationPlanItem(libraryId, name, mediaType, true, false, true, 1, 24, 25, null, null);
        Assert.True(await jobs.RequestLibrarySearchAsync(due, CancellationToken.None));
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

    [Fact]
    public async Task A_due_cycle_finds_a_better_release_for_a_film_on_disk_below_its_profile_and_sends_it_with_the_old_file_kept()
    {
        await using var rig = await RigAsync();
        var films = await RoutedLibraryAsync(rig, "movies", "Films", rig.FilmIndexerId);
        var profile = (await PostOkAsync(rig.App, "/api/quality-profiles", new
        {
            name = "Small 720p films, upgrading", mediaType = "movies", cutoffQuality = "Bluray 720p", allowedQualities = "DVD,Bluray 720p", customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        })).GetProperty("id").GetString()!;
        var held = await ApiScenes.FilmOnDiskAsync(rig.App, "Sintel", 2010, films, quality: "DVD");
        (await rig.App.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { held.MovieId }, qualityProfileId = profile })).EnsureSuccessStatusCode();

        await RunTheCycleAsync(rig, films, "Films", "movies");

        Assert.Equal(1, rig.Adds);
        var added = Assert.Single(rig.Qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.Contains("A779FA758F2229586EEBC2E5960EB32FE5659596", added.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{held.MovieId}' AND status = 'sent';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{held.MovieId}';"));
        Assert.True(File.Exists(held.FilePath), "the file already in the library stays until the better one is imported");
    }

    [Fact]
    public async Task A_due_cycle_finds_a_better_release_for_an_episode_on_disk_below_its_profile_and_sends_it_with_the_old_file_kept()
    {
        await using var rig = await RigAsync();
        var shows = await RoutedLibraryAsync(rig, "tv", "Shows", rig.ShowIndexerId);
        var root = await ApiScenes.LibraryRootAsync(rig.App, shows);
        var file = Path.Combine(root, "Breaking Bad", "Season 01", "Breaking Bad S01E01 [HDTV 720p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            shows, "Breaking Bad", 2008, "upgrade", "Below the profile.", "HDTV 720p", "WEB 1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: file, FileSizeBytes: 4)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;
        // What a quality check leaves behind for a file below its profile: the episode is wanted as an upgrade.
        await ApiScenes.ExecuteAsync(rig.App, "series", $"UPDATE episode_wanted_state SET wanted_status = 'upgrade' WHERE episode_id = '{episodeId}';");

        await RunTheCycleAsync(rig, shows, "Shows", "tv");

        Assert.True(rig.Adds >= 1, string.Join(" | ", rig.Jackett.Requests.Select(r => r.PathAndQuery)));
        Assert.Contains(rig.Jackett.Requests, request => request.PathAndQuery.Contains("t=tvsearch", StringComparison.Ordinal));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{episodeId}' AND status = 'sent';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        Assert.True(File.Exists(file), "the file already in the library stays until the better one is imported");
    }
}
