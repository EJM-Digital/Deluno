using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #753: the dedicated multi-indexer manual Search endpoint -- a free-text
/// query fanned out to a chosen set of enabled indexers, with no title
/// required, returning the same enriched columns (protocol, age,
/// seeders/leechers) the per-title interactive search results show.
/// </summary>
public sealed class ManualSearchEndpointTests
{
    [Fact]
    public async Task An_empty_query_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "  ", searchType = "movie" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_enabled_indexers_returns_an_empty_result_rather_than_an_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "movie" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("indexersQueried").GetInt32());
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task Fans_out_to_a_real_indexer_and_returns_enriched_columns()
    {
        await using var server = await TorznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", server.BaseUrl, "fixture-key", 10, "2000", "", "movies", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "movie", indexerIds = new[] { indexer.Id } });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        Assert.Equal(1, body.GetProperty("indexersQueried").GetInt32());
        var candidatesArray = body.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.True(candidatesArray.Length == 1, $"Expected 1 candidate, got {candidatesArray.Length}. Raw: {raw}");
        var candidate = candidatesArray[0];
        Assert.Equal("Example.Release.WEB.1080p", candidate.GetProperty("releaseName").GetString());
        Assert.Equal("torznab", candidate.GetProperty("indexerProtocol").GetString());
        Assert.Equal(12, candidate.GetProperty("seeders").GetInt32());
        Assert.Equal(3, candidate.GetProperty("leechers").GetInt32());
        // #969: the grab-count column.
        Assert.Equal(47, candidate.GetProperty("grabs").GetInt32());
        // #1172: the hard-rule signals travel with the result so the page can say them in words.
        Assert.True(candidate.TryGetProperty("riskFlags", out _));
        Assert.True(candidate.TryGetProperty("rejectionGate", out _));
    }

    [Fact]
    public async Task A_free_search_never_says_a_release_is_for_something_else_because_the_query_is_not_a_title()
    {
        await using var server = await TorznabServerAsync(
            "The.Simpsons.S38E01.The.Childrens.Book.Job.1080p.WEB-DL.DDP5.1.H.264-GRP",
            "The.Simpsons.S38E02.Some.Cam.Release.CAM.x264-GRP");
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", server.BaseUrl, "fixture-key", 10, "5000", "", "tv", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "The Simpsons S38", searchType = "tv", indexerIds = new[] { indexer.Id } });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var candidates = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray().ToArray();

        Assert.Equal(2, candidates.Length);
        foreach (var candidate in candidates)
        {
            var everything = string.Join(" | ",
                candidate.GetProperty("decisionReasons").EnumerateArray().Select(item => item.GetString())
                    .Concat(candidate.GetProperty("riskFlags").EnumerateArray().Select(item => item.GetString()))
                    .Append(candidate.GetProperty("rejectionGate").ValueKind == JsonValueKind.String ? candidate.GetProperty("rejectionGate").GetString() : null));
            Assert.DoesNotContain("something else", everything);
        }
        var real = candidates.Single(item => item.GetProperty("releaseName").GetString()!.Contains("S38E01"));
        Assert.NotEqual("rejected", real.GetProperty("decisionStatus").GetString());
        // The hard rules still hold: a cam release is refused.
        var cam = candidates.Single(item => item.GetProperty("releaseName").GetString()!.Contains("CAM"));
        Assert.Equal("rejected", cam.GetProperty("decisionStatus").GetString());
    }

    [Fact]
    public async Task Grabbing_without_a_download_url_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = (string?)null,
            searchType = "movie"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Grabbing_with_no_movie_library_at_all_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Add a movie library", body);
    }

    [Fact]
    public async Task Grabbing_routes_through_a_movie_librarys_own_routed_client_without_the_person_picking_one()
    {
        var grabService = new FakeGrabService();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabService);
        });

        var libraryRoot = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = libraryRoot
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal($"qBittorrent on {Deluno.Contracts.DelunoHostName.Current}", body.GetProperty("downloadClientName").GetString());
        Assert.Equal(client.Id, grabService.LastClientId);
        Assert.True(body.GetProperty("succeeded").GetBoolean());

        // #1062: a release sent from the Search page has a dispatch of its own,
        // so Deluno can see, count and clean up the download it started.
        var recorded = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().QueryDispatchesAsync(
            new DispatchQueryFilter { ClientId = client.Id },
            new DispatchPaginationOptions(),
            CancellationToken.None);
        var dispatch = Assert.Single(recorded.Items);
        Assert.Equal("Example.Release.WEB.1080p", dispatch.ReleaseName);
        Assert.Equal(libraryId, dispatch.LibraryId);
        Assert.Equal("movies", dispatch.MediaType);
        Assert.Equal("succeeded", dispatch.GrabStatus);
        Assert.Equal("fake-hash", dispatch.TorrentHashOrItemId);
        Assert.Contains("chosenOnSearch", dispatch.NotesJson);
    }

    [Fact]
    public async Task A_grab_of_a_download_Deluno_already_has_is_followed_not_recorded_a_second_time()
    {
        var grabService = new FakeGrabService(duplicateFollow: true);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabService);
        });
        var libraryRoot = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = libraryRoot });
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already on its way", body.GetProperty("message").GetString());
        var recorded = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().QueryDispatchesAsync(
            new DispatchQueryFilter { ClientId = client.Id },
            new DispatchPaginationOptions(),
            CancellationToken.None);
        Assert.Empty(recorded.Items);
    }

    [Fact]
    public async Task The_category_list_names_movies_and_tv()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/search/categories");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var labels = body.EnumerateArray().Select(item => item.GetProperty("label").GetString()).ToArray();

        Assert.Contains("Movies", labels);
        Assert.Contains("TV", labels);
    }

    [Fact]
    public async Task A_basic_search_asks_both_the_movie_and_tv_engines()
    {
        await using var server = await TorznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", server.BaseUrl, "fixture-key", 10, "2000,5000", "", "both", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "basic", indexerIds = new[] { indexer.Id } });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        var candidatesArray = body.GetProperty("candidates").EnumerateArray().ToArray();
        // The fixture indexer answers every query the same way, so a basic
        // search that asks both engines gets one hit back from each.
        Assert.Equal(2, candidatesArray.Length);
        var searchTypes = candidatesArray.Select(item => item.GetProperty("searchType").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "movies", "tv" }, searchTypes);
    }

    [Fact]
    public async Task A_search_from_the_search_page_is_recorded_in_indexer_history_against_its_indexer()
    {
        // #978: a Search-page search was recorded nowhere, so it never
        // appeared in the combined Indexer History feed -- even though every
        // RSS sync and per-title search already does.
        await using var server = await TorznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", server.BaseUrl, "fixture-key", 10, "2000", "", "movies", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "movie", indexerIds = new[] { indexer.Id } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var history = await app.Client.GetAsync("/api/indexer-history");
        Assert.True(history.IsSuccessStatusCode, await history.Content.ReadAsStringAsync());
        var body = await history.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToArray();

        var searchRow = items.SingleOrDefault(item => item.GetProperty("eventType").GetString() == "search"
            && item.GetProperty("indexerId").GetString() == indexer.Id);
        Assert.NotEqual(default, searchRow);
        Assert.Equal("Fixture torznab", searchRow.GetProperty("indexerName").GetString());
        Assert.Equal("matched", searchRow.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_grab_from_the_search_page_is_recorded_in_indexer_history_against_its_indexer()
    {
        var grabService = new FakeGrabService();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabService);
        });

        var libraryRoot = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = libraryRoot
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            indexerId = "fixture-indexer",
            indexerName = "Fixture torznab",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var history = await app.Client.GetAsync("/api/indexer-history");
        Assert.True(history.IsSuccessStatusCode, await history.Content.ReadAsStringAsync());
        var body = await history.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToArray();

        var grabRow = items.SingleOrDefault(item => item.GetProperty("eventType").GetString() == "grab"
            && item.GetProperty("indexerId").GetString() == "fixture-indexer");
        Assert.NotEqual(default, grabRow);
        Assert.Equal("Fixture torznab", grabRow.GetProperty("indexerName").GetString());
        Assert.Equal("succeeded", grabRow.GetProperty("outcome").GetString());
        Assert.Contains("Example.Release.WEB.1080p", grabRow.GetProperty("message").GetString());
    }

    private sealed class FakeGrabService(bool duplicateFollow = false) : IDownloadClientGrabService
    {
        public string? LastClientId { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            LastClientId = clientId;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.") { ExternalId = "fake-hash", IsDuplicateFollow = duplicateFollow });
        }
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> TorznabServerAsync(params string[] releaseNames)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var names = releaseNames.Length == 0 ? ["Example.Release.WEB.1080p"] : releaseNames;
        var items = string.Concat(names.Select((name, index) =>
            $"""
             <item>
               <title>{name}</title>
               <link>https://fixture.invalid/x{index}.torrent</link>
               <torznab:attr name="seeders" value="12" />
               <torznab:attr name="peers" value="3" />
               <torznab:attr name="grabs" value="47" />
             </item>
             """));
        var feed =
            "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>"
            + items + "</channel></rss>";
        app.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync(feed);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }
}
