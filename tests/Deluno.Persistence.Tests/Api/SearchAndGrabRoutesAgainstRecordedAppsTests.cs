using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Searching, routing and sending a release when the request is right (coverage register, area api), against the real
/// replies of Jackett 0.24.2850 and qBittorrent 5.2.1 (tests/fixtures/recorded) served over real loopback ports: a
/// search reads Jackett's real Torznab feed, the grab sends the release's magnet link (the recorded torrent file's bytes are not kept) to qBittorrent as the real client was asked to.
/// </summary>
public sealed class SearchAndGrabRoutesAgainstRecordedAppsTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Task<RecordedAppServer> JackettAsync()
        => RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.ByQuery("t", ("caps", RecordedReplies.Jackett("caps").Exchanges[0]), ("search", RecordedReplies.Jackett("search-text").Exchanges[0])),
            addressInRecordings: "http://127.0.0.1:9117");

    private static Task<RecordedAppServer> QbittorrentAsync()
        => RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0]));

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> AddIndexerAsync(ApplicationTestHost app, RecordedAppServer jackett)
        => (await PostOkAsync(app, "/api/indexers", new
        {
            name = "YTS via Jackett",
            protocol = "torznab",
            privacy = "public",
            baseUrl = jackett.BaseUrl + TorznabPath,
            apiKey = Key,
            priority = 25,
            categories = "2000",
            tags = "",
            mediaScope = "movies",
            isEnabled = true,
            preferMagnetUrl = true
        })).GetProperty("id").GetString()!;

    private static async Task<string> AddRoutedLibraryAsync(ApplicationTestHost app, RecordedAppServer qbit)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
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
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.GetProperty("id").GetString(), priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        // A client must pass a connection test before a release is sent to it.
        var tested = await app.Client.PostAsync($"/api/download-clients/{client.GetProperty("id").GetString()}/test", content: null);
        Assert.Equal("healthy", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        return libraryId;
    }

    [Fact]
    public async Task Saving_a_librarys_routing_stores_its_download_client_and_a_client_that_does_not_exist_is_refused()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await AddRoutedLibraryAsync(app, qbit);

        var routing = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing");

        var link = Assert.Single(routing.GetProperty("downloadClients").EnumerateArray());
        Assert.Equal(1, link.GetProperty("priority").GetInt32());
        Assert.Equal(qbit.Port, (await app.Client.GetFromJsonAsync<JsonElement>("/api/download-clients")).EnumerateArray().Single().GetProperty("port").GetInt32());

        var cleared = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = Array.Empty<object>() });
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing")).GetProperty("downloadClients").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/routing", new { sources = Array.Empty<object>(), downloadClients = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task A_search_reads_the_real_feed_and_is_recorded_in_indexer_history_until_the_history_is_cleared()
    {
        await using var jackett = await JackettAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = await AddIndexerAsync(app, jackett);

        var search = await PostOkAsync(app, "/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexerId } });

        var candidate = Assert.Single(search.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", candidate.GetProperty("releaseName").GetString());
        Assert.StartsWith("magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596", candidate.GetProperty("downloadUrl").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(jackett.Unanswered);
        var history = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        Assert.Contains(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "search" && item.GetProperty("indexerId").GetString() == indexerId);

        var cleared = await app.Client.PostAsync("/api/indexer-history/clear", content: null);

        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history")).GetProperty("items").EnumerateArray());
        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers")).GetArrayLength());
    }

    [Fact]
    public async Task Sending_a_chosen_search_result_adds_its_magnet_link_to_the_routed_client()
    {
        await using var jackett = await JackettAsync();
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = await AddIndexerAsync(app, jackett);
        await AddRoutedLibraryAsync(app, qbit);
        var search = await PostOkAsync(app, "/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexerId } });
        var candidate = Assert.Single(search.GetProperty("candidates").EnumerateArray());

        var grab = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = candidate.GetProperty("releaseName").GetString(),
            downloadUrl = candidate.GetProperty("downloadUrl").GetString(),
            indexerId,
            indexerName = "YTS via Jackett",
            searchType = "movie",
            sizeBytes = 143151596L
        });

        Assert.True(grab.IsSuccessStatusCode, await grab.Content.ReadAsStringAsync());
        var grabbed = await grab.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(grabbed.GetProperty("succeeded").GetBoolean(), grabbed.ToString());
        var added = Assert.Single(qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.Contains("A779FA758F2229586EEBC2E5960EB32FE5659596", added.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(jackett.Requests, request => request.PathAndQuery.StartsWith("/dl/", StringComparison.Ordinal));
        Assert.Empty(qbit.Unanswered);
        Assert.Empty(jackett.Unanswered);
        var history = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        Assert.Contains(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "grab" && item.GetProperty("indexerId").GetString() == indexerId);

        var noLink = await app.Client.PostAsJsonAsync("/api/search/grab", new { releaseName = "x", downloadUrl = "", searchType = "movie" });
        Assert.Equal(HttpStatusCode.BadRequest, noLink.StatusCode);
    }
}
