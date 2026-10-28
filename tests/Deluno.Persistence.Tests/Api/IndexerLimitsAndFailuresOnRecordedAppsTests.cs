using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: the limits an indexer carries (its query limit, its grab limit, the hourly cap across every indexer) and an indexer
/// that answers with a server error, run through the whole application against the recorded replies of Jackett 0.24.2850 and
/// qBittorrent 5.2.1 (tests/fixtures/recorded) served over real loopback ports. A call a limit holds back is never sent.
/// </summary>
public sealed class IndexerLimitsAndFailuresOnRecordedAppsTests
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

    private static Task<string> AddIndexerAsync(ApplicationTestHost app, RecordedAppServer jackett, int? queryLimit = null, int? grabLimit = null)
        => AddIndexerAtAsync(app, jackett.BaseUrl, queryLimit, grabLimit);

    private static async Task<string> AddIndexerAtAsync(ApplicationTestHost app, string baseUrl, int? queryLimit = null, int? grabLimit = null)
        => (await PostOkAsync(app, "/api/indexers", new
        {
            name = "YTS via Jackett",
            protocol = "torznab",
            privacy = "public",
            baseUrl = baseUrl + TorznabPath,
            apiKey = Key,
            priority = 25,
            categories = "2000",
            tags = "",
            mediaScope = "movies",
            isEnabled = true,
            preferMagnetUrl = true,
            queryLimit,
            grabLimit,
            limitsUnit = "day"
        })).GetProperty("id").GetString()!;

    private static async Task AddRoutedLibraryAsync(ApplicationTestHost app, RecordedAppServer qbit)
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
    }

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string indexerId)
        => await PostOkAsync(app, "/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexerId } });

    private static int SearchCalls(RecordedAppServer server) => server.Requests.Count(request => request.PathAndQuery.Contains("t=search", StringComparison.Ordinal));

    [Fact]
    public async Task The_hourly_cap_across_every_indexer_holds_back_the_next_search_which_is_never_sent_and_is_said_in_words()
    {
        await using var jackett = await JackettAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = await AddIndexerAsync(app, jackett);
        var saved = await app.Client.PatchAsJsonAsync("/api/settings", new { maxSearchesPerHourEnabled = true, maxSearchesPerHour = 1 });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

        var first = await SearchAsync(app, indexerId);
        var second = await SearchAsync(app, indexerId);

        Assert.Single(first.GetProperty("candidates").EnumerateArray());
        Assert.Empty(second.GetProperty("candidates").EnumerateArray());
        Assert.Equal(1, SearchCalls(jackett));
        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard");
        var row = board.GetProperty("indexers").EnumerateArray().Single();
        Assert.Equal(1, row.GetProperty("totalQueries").GetInt32());
        Assert.Equal(0, row.GetProperty("failedQueries").GetInt32());
        Assert.Equal(1, row.GetProperty("heldBackQueries").GetInt32());
    }

    [Fact]
    public async Task An_indexers_grab_limit_refuses_the_next_send_in_plain_words_before_the_client_is_asked_anything()
    {
        await using var jackett = await JackettAsync();
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = await AddIndexerAsync(app, jackett, grabLimit: 1);
        await AddRoutedLibraryAsync(app, qbit);
        var candidate = Assert.Single((await SearchAsync(app, indexerId)).GetProperty("candidates").EnumerateArray());
        object Send(string releaseName) => new
        {
            releaseName,
            downloadUrl = candidate.GetProperty("downloadUrl").GetString(),
            indexerId,
            indexerName = "YTS via Jackett",
            searchType = "movie",
            sizeBytes = 143151596L
        };

        var first = await PostOkAsync(app, "/api/search/grab", Send(candidate.GetProperty("releaseName").GetString()!));
        var addsAfterFirst = qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        var second = await PostOkAsync(app, "/api/search/grab", Send(candidate.GetProperty("releaseName").GetString() + " second"));

        Assert.True(first.GetProperty("succeeded").GetBoolean(), first.ToString());
        Assert.Equal(1, addsAfterFirst);
        Assert.False(second.GetProperty("succeeded").GetBoolean(), second.ToString());
        Assert.Contains("grab limit (1 per day) is reached", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal)));
    }

    /// <summary>The 500 is what Jackett recorded for an indexer it has not set up (unknown-indexer); it is a server error, not a refused key.</summary>
    [Fact]
    public async Task A_server_error_from_an_indexer_is_tried_three_times_and_after_two_failed_searches_the_indexer_is_paused_and_not_asked()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        server.AnswerEverythingWith = RecordedReplies.Jackett("unknown-indexer").Single.Reply;
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = await AddIndexerAtAsync(app, server.BaseUrl);

        var first = await SearchAsync(app, indexerId);
        var callsAfterFirst = server.Requests.Count;
        var second = await SearchAsync(app, indexerId);
        var callsAfterSecond = server.Requests.Count;
        var third = await SearchAsync(app, indexerId);

        var failure = Assert.Single(first.GetProperty("failures").EnumerateArray());
        Assert.Equal("Unavailable", failure.GetProperty("kind").GetString());
        Assert.Equal(500, failure.GetProperty("httpStatus").GetInt32());
        Assert.True(failure.GetProperty("isTransient").GetBoolean());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
        Assert.Equal(3, callsAfterFirst);
        Assert.Empty(first.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Unavailable", Assert.Single(second.GetProperty("failures").EnumerateArray()).GetProperty("kind").GetString());
        Assert.Equal(6, callsAfterSecond);
        var paused = Assert.Single(third.GetProperty("failures").EnumerateArray());
        Assert.Equal("CircuitOpen", paused.GetProperty("kind").GetString());
        Assert.Contains("Deluno paused it after repeated failures", paused.GetProperty("message").GetString());
        Assert.Equal(6, server.Requests.Count);
    }
}
