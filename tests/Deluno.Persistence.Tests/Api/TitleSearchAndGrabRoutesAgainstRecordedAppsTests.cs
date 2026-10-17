using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Searching for one film or show and sending a chosen release when the request is right (coverage register, area api),
/// against the real replies of Jackett 0.24.2850 and qBittorrent 5.2.1 (tests/fixtures/recorded) served over real
/// loopback ports: Jackett's real Torznab results for Sintel and for Breaking Bad S01E01, and qBittorrent taking the add.
/// </summary>
public sealed class TitleSearchAndGrabRoutesAgainstRecordedAppsTests
{
    private const string FilmPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string ShowPath = "/api/v2.0/indexers/thepiratebay/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private sealed class Rig(RecordedAppServer jackett, RecordedAppServer qbit, ApplicationTestHost app, string filmLibrary, string showLibrary) : IAsyncDisposable
    {
        public RecordedAppServer Jackett { get; } = jackett;

        public RecordedAppServer Qbit { get; } = qbit;

        public ApplicationTestHost App { get; } = app;

        public string FilmLibrary { get; } = filmLibrary;

        public string ShowLibrary { get; } = showLibrary;

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
                // A plain text search is answered by the tracker it was sent to: the film tracker's own text search, the show tracker's own.
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
            new[] { "sign-in", "version", "settings", "resumed" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0]));
        var app = await ApplicationTestHost.StartAsync();

        async Task<string> IndexerAsync(string path, string scope, string categories)
        {
            var created = await PostOkAsync(app, "/api/indexers", new
            {
                protocol = "torznab",
                privacy = "public",
                baseUrl = jackett.BaseUrl + path,
                apiKey = Key,
                priority = 25,
                categories,
                tags = "",
                mediaScope = scope,
                isEnabled = true,
                preferMagnetUrl = true
            });
            var id = created.GetProperty("id").GetString()!;
            // A source must pass a connection test before Deluno searches through it.
            Assert.Equal("healthy", (await PostOkAsync(app, $"/api/indexers/{id}/test")).GetProperty("healthStatus").GetString());
            return id;
        }

        var client = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent",
            host = qbit.Host,
            port = qbit.Port,
            username = "deluno",
            password = "a-test-password",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        });
        var clientId = client.GetProperty("id").GetString()!;
        Assert.Equal("healthy", (await (await app.Client.PostAsync($"/api/download-clients/{clientId}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());

        async Task<string> LibraryAsync(string mediaType, string indexerId)
        {
            var libraryId = await ApiScenes.CreateLibraryAsync(app, mediaType);
            var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
            {
                sources = new[] { new { indexerId, priority = 1, requiredTags = "", excludedTags = "" } },
                downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
            });
            Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
            return libraryId;
        }

        var filmLibrary = await LibraryAsync("movies", await IndexerAsync(FilmPath, "movies", "2000"));
        var showLibrary = await LibraryAsync("tv", await IndexerAsync(ShowPath, "tv", "5000"));
        return new Rig(jackett, qbit, app, filmLibrary, showLibrary);
    }

    private static async Task<string> AddFilmAsync(Rig rig)
        => (await PostOkAsync(rig.App, "/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = rig.FilmLibrary })).GetProperty("id").GetString()!;

    private static async Task<(string SeriesId, string EpisodeId)> AddShowAsync(Rig rig)
    {
        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            rig.ShowLibrary, "Breaking Bad", 2008, "missing", "Wanted.", null, "WEB 1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: false)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;
        return (seriesId, episodeId);
    }

    [Fact]
    public async Task Previewing_a_films_search_lists_what_the_real_indexer_found_for_its_imdb_id_and_sends_nothing()
    {
        await using var rig = await RigAsync();
        var filmId = await AddFilmAsync(rig);
        var addsBefore = rig.Qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));

        var preview = await PostOkAsync(rig.App, $"/api/movies/{filmId}/search?mode=preview");

        var candidate = Assert.Single(preview.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", candidate.GetProperty("releaseName").GetString());
        Assert.True(rig.Jackett.Requests.Any(request => request.PathAndQuery.Contains("/yts/", StringComparison.Ordinal) && request.PathAndQuery.Contains("q=Sintel", StringComparison.Ordinal)), string.Join(" | ", rig.Jackett.Requests.Select(r => r.PathAndQuery)));
        Assert.Equal(addsBefore, rig.Qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal)));
        Assert.Empty(rig.Jackett.Unanswered);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsync("/api/movies/no-such-film/search?mode=preview", content: null)).StatusCode);
    }

    [Fact]
    public async Task Sending_a_chosen_release_for_a_film_adds_it_to_the_routed_client_and_records_the_send()
    {
        await using var rig = await RigAsync();
        var filmId = await AddFilmAsync(rig);
        var preview = await PostOkAsync(rig.App, $"/api/movies/{filmId}/search?mode=preview");
        var candidate = Assert.Single(preview.GetProperty("candidates").EnumerateArray());

        var grab = await PostOkAsync(rig.App, $"/api/movies/{filmId}/grab", new
        {
            releaseName = candidate.GetProperty("releaseName").GetString(),
            indexerId = candidate.GetProperty("indexerId").GetString(),
            indexerName = candidate.GetProperty("indexerName").GetString(),
            downloadUrl = candidate.GetProperty("downloadUrl").GetString(),
            candidateQuality = candidate.GetProperty("quality").GetString(),
            sizeBytes = 143151596L,
            force = true,
            overrideReason = "You chose this release."
        });

        Assert.Equal(candidate.GetProperty("releaseName").GetString(), grab.GetProperty("releaseName").GetString());
        var added = Assert.Single(rig.Qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.Contains("A779FA758F2229586EEBC2E5960EB32FE5659596", added.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Qbit.Unanswered);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{filmId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsJsonAsync("/api/movies/no-such-film/grab", new { releaseName = "x", downloadUrl = "magnet:?xt=urn:btih:00" })).StatusCode);
    }

    [Fact]
    public async Task Searching_a_show_sends_the_best_real_release_to_the_client_and_a_preview_sends_nothing()
    {
        await using var rig = await RigAsync();
        var (seriesId, _) = await AddShowAsync(rig);

        var preview = await PostOkAsync(rig.App, $"/api/series/{seriesId}/search?mode=preview");
        Assert.True(preview.GetProperty("candidates").GetArrayLength() > 0);
        Assert.DoesNotContain(rig.Qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));

        var sent = await PostOkAsync(rig.App, $"/api/series/{seriesId}/search");

        Assert.True(sent.GetProperty("candidates").GetArrayLength() > 0);
        Assert.Contains(rig.Jackett.Requests, request => request.PathAndQuery.Contains("/thepiratebay/", StringComparison.Ordinal) && request.PathAndQuery.Contains("t=search&q=Breaking%20Bad", StringComparison.Ordinal));
        Assert.Empty(rig.Jackett.Unanswered);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsync("/api/series/no-such-show/search", content: null)).StatusCode);
    }

    [Fact]
    public async Task Search_now_on_a_show_queues_one_search_for_each_aired_missing_episode_and_leaves_future_unmonitored_and_held_ones_alone()
    {
        await using var rig = await RigAsync();
        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            rig.ShowLibrary, "Breaking Bad", 2008, "missing", "Wanted.", null, "WEB 1080p", false, null, null,
            [
                new ImportedEpisodeItem(1, 1, HasFile: false, AirDate: new DateOnly(2008, 1, 20)),
                new ImportedEpisodeItem(1, 2, HasFile: false, AirDate: new DateOnly(2008, 1, 27)),
                new ImportedEpisodeItem(1, 3, HasFile: true, FilePath: "/shows/Breaking Bad/S01E03.mkv", FileSizeBytes: 4, AirDate: new DateOnly(2008, 2, 3)),
                new ImportedEpisodeItem(1, 4, HasFile: false, AirDate: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2)))
            ],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        // The air dates a metadata sync would have written: two long past, one past (held), one two years ahead.
        await ApiScenes.ExecuteAsync(rig.App, "series", $"UPDATE episode_entries SET air_date_utc = CASE episode_number WHEN 4 THEN '{DateTime.UtcNow.AddYears(2):O}' ELSE '2008-02-0' || episode_number || 'T00:00:00.0000000+00:00' END WHERE series_id = '{seriesId}';");

        var sent = await PostOkAsync(rig.App, $"/api/series/{seriesId}/search");

        Assert.Equal(2, sent.GetProperty("plannedEpisodes").GetInt32());
        Assert.Equal(2, await ApiScenes.ScalarAsync(rig.App, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'episode.search' AND status = 'queued';"));
        var episodes = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes;
        var queued = await ApiScenes.TextAsync(rig.App, "jobs", "SELECT group_concat(payload_json, ' ') FROM job_queue WHERE job_type = 'episode.search';");
        foreach (var wanted in episodes.Where(episode => episode.EpisodeNumber is 1 or 2)) Assert.Contains(wanted.EpisodeId, queued, StringComparison.Ordinal);
        foreach (var left in episodes.Where(episode => episode.EpisodeNumber is 3 or 4)) Assert.DoesNotContain(left.EpisodeId, queued, StringComparison.Ordinal);

        // Pressing it again while those jobs are still waiting adds none.
        await PostOkAsync(rig.App, $"/api/series/{seriesId}/search");
        Assert.Equal(2, await ApiScenes.ScalarAsync(rig.App, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'episode.search';"));

        // A preview plans nothing.
        var preview = await PostOkAsync(rig.App, $"/api/series/{seriesId}/search?mode=preview");
        Assert.Equal(0, preview.GetProperty("plannedEpisodes").GetInt32());
    }

    [Fact]
    public async Task Searching_chosen_episodes_and_one_season_asks_the_real_indexer_for_them_and_reports_what_it_matched()
    {
        await using var rig = await RigAsync();
        var (seriesId, episodeId) = await AddShowAsync(rig);

        var episodes = await PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });
        Assert.Equal(1, episodes.GetProperty("searchedEpisodes").GetInt32());
        Assert.True(episodes.GetProperty("matchedCount").GetInt32() >= 1, episodes.ToString());

        var season = await PostOkAsync(rig.App, $"/api/series/{seriesId}/seasons/1/search");
        Assert.Equal(1, season.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, season.GetProperty("searchedEpisodes").GetInt32());
        Assert.Contains(rig.Jackett.Requests, request => request.PathAndQuery.Contains("t=tvsearch", StringComparison.Ordinal));
        Assert.Empty(rig.Jackett.Unanswered);

        var none = await rig.App.Client.PostAsJsonAsync($"/api/series/{seriesId}/episodes/search", new { episodeIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.App.Client.PostAsync($"/api/series/{seriesId}/seasons/9/search", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsync("/api/series/no-such-show/seasons/1/search", content: null)).StatusCode);
    }

    [Fact]
    public async Task Sending_a_chosen_release_for_a_show_adds_it_to_the_routed_client()
    {
        await using var rig = await RigAsync();
        var (seriesId, _) = await AddShowAsync(rig);
        var preview = await PostOkAsync(rig.App, $"/api/series/{seriesId}/search?mode=preview");
        var candidate = preview.GetProperty("candidates").EnumerateArray().First(item => item.GetProperty("releaseName").GetString()!.StartsWith("Breaking.Bad.S01E01.Pilot.1080p.HEVC", StringComparison.Ordinal));

        var grab = await PostOkAsync(rig.App, $"/api/series/{seriesId}/grab", new
        {
            releaseName = candidate.GetProperty("releaseName").GetString(),
            indexerId = candidate.GetProperty("indexerId").GetString(),
            indexerName = candidate.GetProperty("indexerName").GetString(),
            downloadUrl = candidate.GetProperty("downloadUrl").GetString(),
            candidateQuality = candidate.GetProperty("quality").GetString(),
            sizeBytes = 1193000000L,
            force = true,
            overrideReason = "You chose this release."
        });

        Assert.Equal(candidate.GetProperty("releaseName").GetString(), grab.GetProperty("releaseName").GetString());
        Assert.Single(rig.Qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.Empty(rig.Qbit.Unanswered);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{seriesId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsJsonAsync("/api/series/no-such-show/grab", new { releaseName = "x", downloadUrl = "magnet:?xt=urn:btih:00" })).StatusCode);
    }

    [Fact]
    public async Task Approving_a_held_release_sends_exactly_that_release_to_the_client_and_takes_it_off_the_needs_action_list()
    {
        await using var rig = await RigAsync();
        var (seriesId, _) = await AddShowAsync(rig);
        // The search itself leaves the real result Jackett gave as held: usable, but wanting a person's yes.
        await PostOkAsync(rig.App, $"/api/series/{seriesId}/search?mode=preview");
        var held = await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases");
        var row = held.EnumerateArray().First();
        var releaseName = row.GetProperty("releaseName").GetString()!;
        var addsBefore = rig.Qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));

        var approve = await rig.App.Client.PostAsync($"/api/attention/held-releases/{row.GetProperty("historyId").GetString()}/approve", content: null);

        Assert.True(approve.IsSuccessStatusCode, await approve.Content.ReadAsStringAsync());
        Assert.Equal(releaseName, (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseName").GetString());
        var added = rig.Qbit.Requests.Where(request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal)).ToArray();
        Assert.Equal(addsBefore + 1, added.Length);
        Assert.Contains(row.GetProperty("downloadUrl").GetString()!.Split('&')[0], Uri.UnescapeDataString(added[^1].Body), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Qbit.Unanswered);
        Assert.DoesNotContain((await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases")).EnumerateArray(), item => item.GetProperty("historyId").GetString() == row.GetProperty("historyId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await rig.App.Client.PostAsync("/api/attention/held-releases/no-such-record/approve", content: null)).StatusCode);
    }
}
