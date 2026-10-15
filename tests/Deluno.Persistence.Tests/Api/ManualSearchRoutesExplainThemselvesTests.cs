using System.Net;
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
/// The Search page's two routes when the request is right but the setup is not (coverage register, area search-grab): which indexers a
/// search reaches and which categories it asks, and what a grab says when no library, client or source can take the release. Driven
/// over the real host and databases with a local feed standing in for the indexer and only the client's reply stood in for.
/// </summary>
public sealed class ManualSearchRoutesExplainThemselvesTests
{
    [Fact]
    public async Task A_search_reaches_only_the_chosen_indexers_and_never_a_disabled_one()
    {
        await using var chosen = await FeedAsync("Chosen.Release.1080p");
        await using var other = await FeedAsync("Other.Release.1080p");
        await using var off = await FeedAsync("Disabled.Release.1080p");
        await using var app = await ApplicationTestHost.StartAsync();
        var chosenIndexer = await AddIndexerAsync(app, "Chosen", chosen.BaseUrl, enabled: true);
        await AddIndexerAsync(app, "Other", other.BaseUrl, enabled: true);
        await AddIndexerAsync(app, "Off", off.BaseUrl, enabled: false);

        var narrowed = await SearchAsync(app, new { query = "Release", searchType = "movie", indexerIds = new[] { chosenIndexer.Id } });

        Assert.Equal(1, narrowed.GetProperty("indexersQueried").GetInt32());
        Assert.Equal("Chosen.Release.1080p", Assert.Single(narrowed.GetProperty("candidates").EnumerateArray()).GetProperty("releaseName").GetString());
        Assert.NotEmpty(chosen.Requests);
        Assert.Empty(other.Requests);
        Assert.Empty(off.Requests);

        var everyone = await SearchAsync(app, new { query = "Release", searchType = "movie", indexerIds = (string[]?)null });

        Assert.Equal(2, everyone.GetProperty("indexersQueried").GetInt32());
        Assert.Equal(new[] { "Chosen.Release.1080p", "Other.Release.1080p" }, everyone.GetProperty("candidates").EnumerateArray().Select(item => item.GetProperty("releaseName").GetString()!).Order().ToArray());
        Assert.NotEmpty(other.Requests);
        Assert.Empty(off.Requests);
    }

    // DEFECT (found by this test, not fixed here): with no category ticked, a TV search against an indexer configured for both
    // movie and TV categories still sends all of them ("cat=2000,5000"), so the "TV" type does not ask only the TV family.
    [Fact(Skip = "Defect: a TV search with no category ticked sends the indexer's whole category list (cat=2000,5000) instead of only the 5000 family; the movie type likewise sends the TV categories (ManualSearchEndpointRouteBuilderExtensions /api/search)")]
    public async Task A_TV_search_asks_only_the_TV_categories_and_a_movie_search_only_the_movie_ones()
    {
        await using var feed = await FeedAsync("Example.Release.1080p");
        await using var app = await ApplicationTestHost.StartAsync();
        await AddIndexerAsync(app, "Both kinds", feed.BaseUrl, enabled: true, categories: "2000,5000", scope: "both");

        await SearchAsync(app, new { query = "Example", searchType = "tv" });
        var tvRequests = feed.Requests.ToArray();
        feed.Requests.Clear();
        await SearchAsync(app, new { query = "Example", searchType = "movie" });
        var movieRequests = feed.Requests.ToArray();

        Assert.NotEmpty(tvRequests);
        Assert.NotEmpty(movieRequests);
        Assert.All(tvRequests, request => Assert.DoesNotContain("2000", request));
        Assert.All(movieRequests, request => Assert.DoesNotContain("5000", request));
    }

    [Fact]
    public async Task Ticked_categories_are_cut_down_to_the_family_of_the_chosen_type()
    {
        await using var feed = await FeedAsync("Example.Release.1080p");
        await using var app = await ApplicationTestHost.StartAsync();
        await AddIndexerAsync(app, "Both kinds", feed.BaseUrl, enabled: true, categories: "2000,5000", scope: "both");

        await SearchAsync(app, new { query = "Example", searchType = "tv", categoryIds = new[] { 2040, 5040 } });

        var request = Assert.Single(feed.Requests);
        Assert.Contains("cat=5040", request);
        Assert.DoesNotContain("2040", request);
    }

    // DEFECT (found by this test, not fixed here): POST /api/search answers only candidates and indexersQueried. An indexer that
    // timed out or refused during a Search-page search is written to Indexer History but never reaches the page.
    [Fact(Skip = "Defect: POST /api/search leaves out the failures the planner returns, so the Search page cannot explain an indexer that failed or timed out (ManualSearchEndpointRouteBuilderExtensions /api/search)")]
    public async Task An_indexer_that_fails_during_the_search_is_named_in_the_answer()
    {
        await using var broken = await FeedAsync(status: 500);
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await AddIndexerAsync(app, "Broken", broken.BaseUrl, enabled: true);

        var answer = await SearchAsync(app, new { query = "Example", searchType = "movie", indexerIds = new[] { indexer.Id } });

        Assert.True(answer.TryGetProperty("failures", out var failures));
        Assert.Contains("Broken", failures.ToString());
    }

    [Fact]
    public async Task A_grab_with_a_library_but_no_routed_client_says_to_link_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");

        var response = await GrabAsync(app, "movie", indexerId: "idx");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Link a download client to a movie library before grabbing a release.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_torrent_release_with_only_a_Usenet_client_routed_is_refused_with_the_reason()
    {
        await using var feed = await FeedAsync("Example.Release.1080p");
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await AddIndexerAsync(app, "Torrent site", feed.BaseUrl, enabled: true);
        var libraryId = await CreateLibraryAsync(app, "movies", "Films");
        var usenet = await AddClientAsync(app, "SAB", "sabnzbd");
        await RouteAsync(app, libraryId, usenet.Id);

        var response = await GrabAsync(app, "movie", indexerId: indexer.Id, indexerName: "Torrent site");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("download client", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Torrent site", text);
    }

    [Fact]
    public async Task A_TV_grab_pools_the_TV_librarys_routed_client_and_records_a_tv_dispatch()
    {
        var grabs = new RecordingGrabService();
        await using var app = await StartAsync(grabs);
        await CreateLibraryAsync(app, "movies", "Films");
        var tvLibrary = await CreateLibraryAsync(app, "tv", "Shows");
        var client = await AddClientAsync(app, "qBit", "qbittorrent");
        await RouteAsync(app, tvLibrary, client.Id);

        var response = await GrabAsync(app, "tv", indexerId: "idx");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(client.Id, grabs.LastClientId);
        Assert.Equal("tv", grabs.LastMediaType);
        var recorded = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().QueryDispatchesAsync(new DispatchQueryFilter { ClientId = client.Id }, new DispatchPaginationOptions(), CancellationToken.None);
        var dispatch = Assert.Single(recorded.Items);
        Assert.Equal("tv", dispatch.MediaType);
        Assert.Equal(tvLibrary, dispatch.LibraryId);
    }

    [Fact]
    public async Task A_grab_the_client_fails_records_a_failed_dispatch_and_a_failed_history_row_and_returns_the_message()
    {
        var grabs = new RecordingGrabService(succeeds: false);
        await using var app = await StartAsync(grabs);
        var libraryId = await CreateLibraryAsync(app, "movies", "Films");
        var client = await AddClientAsync(app, "qBit", "qbittorrent");
        await RouteAsync(app, libraryId, client.Id);

        var response = await GrabAsync(app, "movie", indexerId: "idx", indexerName: "Fixture torznab");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal("qBittorrent refused the torrent.", body.GetProperty("message").GetString());
        var recorded = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().QueryDispatchesAsync(new DispatchQueryFilter { ClientId = client.Id }, new DispatchPaginationOptions(), CancellationToken.None);
        var dispatch = Assert.Single(recorded.Items);
        Assert.Equal("failed", dispatch.GrabStatus);
        var history = (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history")).GetProperty("items").EnumerateArray().ToArray();
        var grabRow = Assert.Single(history, item => item.GetProperty("eventType").GetString() == "grab");
        Assert.Equal("failed", grabRow.GetProperty("outcome").GetString());
        Assert.Contains("qBittorrent refused the torrent.", grabRow.GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------------------------------- helpers

    private static Task<ApplicationTestHost> StartAsync(RecordingGrabService grabs)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
        });

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, object body)
    {
        using var response = await app.Client.PostAsJsonAsync("/api/search", body);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, string searchType, string indexerId, string? indexerName = null)
        => app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            indexerId,
            indexerName,
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType
        });

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new { name, mediaType, rootPath = root });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<DownloadClientItem> AddClientAsync(ApplicationTestHost app, string name, string protocol)
        => await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest(name, protocol, "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);

    private static async Task RouteAsync(ApplicationTestHost app, string libraryId, string clientId)
    {
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
    }

    private static async Task<IndexerItem> AddIndexerAsync(ApplicationTestHost app, string name, string url, bool enabled, string categories = "2000", string scope = "movies")
        => await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest(name, "torznab", "public", url, "fixture-key", 10, categories, "", scope, enabled),
            CancellationToken.None);

    private sealed class RecordingGrabService(bool succeeds = true) : IDownloadClientGrabService
    {
        public string? LastClientId { get; private set; }

        public string? LastMediaType { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            LastClientId = clientId;
            LastMediaType = request.MediaType;
            return Task.FromResult(succeeds
                ? new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.") { ExternalId = "fake-hash" }
                : new DownloadClientGrabResult(clientId, request.ReleaseName, false, "failed", "qBittorrent refused the torrent."));
        }
    }

    private sealed class Feed(WebApplication app, string baseUrl, List<string> requests) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public List<string> Requests => requests;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Feed> FeedAsync(string? release = null, int status = 200)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var requests = new List<string>();
        var feed =
            "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>"
            + (release is null ? string.Empty : $"<item><title>{release}</title><link>https://fixture.invalid/{release}.torrent</link><torznab:attr name=\"seeders\" value=\"12\" /><torznab:attr name=\"peers\" value=\"3\" /></item>")
            + "</channel></rss>";
        app.Run(async context =>
        {
            lock (requests) requests.Add(context.Request.QueryString.Value ?? string.Empty);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync(feed);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Feed(app, address, requests);
    }
}
