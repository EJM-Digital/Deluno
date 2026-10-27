using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: an indexer with an HTTP proxy assigned is tested, searched and read for its feed through that proxy, against what a
/// real Torznab indexer (Jackett 0.24.2850) answered. The local server stands in for the proxy: an indexer address whose host
/// does not exist can only be answered by it, so a call that arrives there left Deluno through the proxy, and an indexer with no
/// proxy (or a switched-off one) at the same address reaches nothing.
/// </summary>
public sealed class IndexersGoOutThroughTheirProxyOnRecordedJackettTests
{
    private const string Key = "k-secret-1";
    private const string NoSuchHost = "http://jackett.invalid:9117/api/v2.0/indexers/yts/results/torznab/api";

    private static async Task<(RecordedIndexerServer Proxy, ApplicationTestHost App, IndexerItem Indexer, ProxyItem Assigned)> StartAsync(bool proxyEnabled = true, bool assignProxy = true)
    {
        var proxyServer = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var proxy = await connections.CreateProxyAsync(new CreateProxyRequest("Local proxy", ProxyTypes.Http, proxyServer.BaseUrl, null, null), CancellationToken.None);
        if (!proxyEnabled)
        {
            await connections.UpdateProxyAsync(proxy.Id, new UpdateProxyRequest(null, null, null, null, null, null, false), CancellationToken.None);
        }

        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Jackett YTS", "torznab", "public", NoSuchHost, Key, 10, "2000", "", "movies", true) { ProxyId = assignProxy ? proxy.Id : null },
            CancellationToken.None);
        return (proxyServer, app, indexer, proxy);
    }

    private static string QueryOf(string request, string name)
        => System.Web.HttpUtility.ParseQueryString(request[(request.IndexOf('?') + 1)..])[name] ?? string.Empty;

    [Fact]
    public async Task The_test_button_asks_the_indexer_through_its_proxy_and_a_healthy_answer_comes_back()
    {
        var (proxy, app, indexer, _) = await StartAsync();
        await using var _ = proxy; await using var __ = app;

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        var asked = Assert.Single(proxy.Requests, request => QueryOf(request, "t") == "search");
        Assert.StartsWith("/api/v2.0/indexers/yts/results/torznab/api", asked, StringComparison.Ordinal);
        Assert.Equal(Key, QueryOf(asked, "apikey"));
    }

    [Fact]
    public async Task A_search_goes_out_through_the_proxy_and_lists_what_the_indexer_answered()
    {
        var (proxy, app, indexer, _) = await StartAsync();
        await using var _ = proxy; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(body.GetProperty("candidates").EnumerateArray());
        Assert.Empty(body.GetProperty("failures").EnumerateArray());
        Assert.Contains(proxy.Requests, request => QueryOf(request, "t") == "search" && QueryOf(request, "q").Contains("Sintel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_rss_feed_read_goes_out_through_the_proxy()
    {
        var (proxy, app, indexer, _) = await StartAsync();
        await using var _ = proxy; await using var __ = app;
        using var scope = app.Services.CreateScope();

        var feed = await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(indexer, CancellationToken.None);

        Assert.True(feed.Read, feed.Problem);
        Assert.NotEmpty(feed.Releases);
        var asked = Assert.Single(proxy.Requests);
        Assert.Equal("search", QueryOf(asked, "t"));
        Assert.Equal(string.Empty, QueryOf(asked, "q"));
    }

    [Fact]
    public async Task The_same_indexer_with_no_proxy_or_a_switched_off_one_reaches_nothing_and_says_it_could_not_be_reached()
    {
        foreach (var (enabled, assign) in new[] { (true, false), (false, true) })
        {
            var (proxy, app, indexer, _) = await StartAsync(proxyEnabled: enabled, assignProxy: assign);
            await using var _ = proxy; await using var __ = app;

            var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.NotEqual("healthy", body.GetProperty("healthStatus").GetString());
            Assert.Equal("connectivity", body.GetProperty("failureCategory").GetString());
            Assert.Empty(proxy.Requests);
        }
    }
}
