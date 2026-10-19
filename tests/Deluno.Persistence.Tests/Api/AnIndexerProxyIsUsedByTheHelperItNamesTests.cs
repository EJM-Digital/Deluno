using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search.Trackers;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// An indexer proxy of the FlareSolverr kind is where a Cloudflare-protected indexer's pages are fetched (#976). Added, edited and removed
/// through the real routes, the proxy is then used the way a search uses it: its address is resolved from what was saved, and a real
/// FlareSolverr 3.5.2 (the loopback server below serves what it recorded) is asked to fetch the page.
/// </summary>
public sealed class AnIndexerProxyIsUsedByTheHelperItNamesTests
{
    private static readonly Uri Tracker = new("https://example.com/");

    private static Task<RecordedAppServer> FlareSolverrAsync(params string[] states)
        => RecordedAppServer.StartAsync("flaresolverr", states.Select(RecordedReplies.Flaresolverr));

    private static async Task<IndexerItem> IndexerBehindAsync(ApplicationTestHost app, string proxyId)
        => await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Behind a challenge", "torznab", "public", "http://indexer.invalid/api", "key", 25, "2000", "", "movies", IsEnabled: true, ProxyId: proxyId),
            CancellationToken.None);

    /// <summary>What a search does to find the helper for an indexer: the saved proxy, turned into the address the helper is asked at.</summary>
    private static async Task<string?> HelperAddressForAsync(ApplicationTestHost app, IndexerItem indexer)
    {
        var current = (await app.Services.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(CancellationToken.None)).Single(item => item.Id == indexer.Id);
        var proxy = await IndexerProxyResolver.ResolveAsync(current, app.Services.GetRequiredService<IConnectionsRepository>(), CancellationToken.None);
        return proxy is not null && string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase)
            ? ProxyAddressFormat.ToUrl(ProxyTypes.Http, proxy.Address)
            : null;
    }

    [Fact]
    public async Task A_proxy_added_through_the_api_is_the_helper_that_fetches_the_page_and_the_page_comes_back_solved()
    {
        await using var flaresolverr = await FlareSolverrAsync("index", "session-created", "request-get-ok");
        await using var app = await ApplicationTestHost.StartAsync();

        var added = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = flaresolverr.BaseUrl });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var proxyId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var indexer = await IndexerBehindAsync(app, proxyId);

        var helperAddress = await HelperAddressForAsync(app, indexer);
        Assert.Equal(flaresolverr.BaseUrl, helperAddress!.TrimEnd('/'));
        var solution = await app.Services.GetRequiredService<ICloudflareHelper>().FetchAsync(Tracker, CancellationToken.None, proxy: null, helperUrl: helperAddress);

        Assert.NotNull(solution);
        Assert.Equal(HttpStatusCode.OK, solution!.Status);
        Assert.Contains("Example Domain", solution.Html);
        var asked = flaresolverr.Requests.Single(request => request.Body.Contains("request.get", StringComparison.Ordinal));
        Assert.Contains("https://example.com/", asked.Body);
        Assert.Empty(flaresolverr.Unanswered);
    }

    [Fact]
    public async Task Editing_the_proxys_address_points_the_next_fetch_at_the_new_helper_and_the_old_one_is_no_longer_asked()
    {
        await using var oldHelper = await FlareSolverrAsync("index", "session-created", "request-get-ok");
        await using var newHelper = await FlareSolverrAsync("index", "session-created", "request-get-ok");
        await using var app = await ApplicationTestHost.StartAsync();
        var proxyId = (await (await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = oldHelper.BaseUrl }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var indexer = await IndexerBehindAsync(app, proxyId);

        var edited = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{proxyId}", new { address = newHelper.BaseUrl, isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var helperAddress = await HelperAddressForAsync(app, indexer);
        Assert.Equal(newHelper.BaseUrl, helperAddress!.TrimEnd('/'));
        var solution = await app.Services.GetRequiredService<ICloudflareHelper>().FetchAsync(Tracker, CancellationToken.None, proxy: null, helperUrl: helperAddress);
        Assert.NotNull(solution);
        Assert.Contains(newHelper.Requests, request => request.Body.Contains("request.get", StringComparison.Ordinal));
        Assert.Empty(oldHelper.Requests);
    }

    [Fact]
    public async Task A_proxy_switched_off_is_not_used_and_a_proxy_removed_leaves_the_indexer_going_direct()
    {
        await using var flaresolverr = await FlareSolverrAsync("index", "session-created", "request-get-ok");
        await using var app = await ApplicationTestHost.StartAsync();
        var proxyId = (await (await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = flaresolverr.BaseUrl }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var indexer = await IndexerBehindAsync(app, proxyId);

        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{proxyId}", new { isEnabled = false })).StatusCode);
        Assert.Null(await HelperAddressForAsync(app, indexer));

        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{proxyId}", new { isEnabled = true })).StatusCode);
        Assert.NotNull(await HelperAddressForAsync(app, indexer));

        var removed = await app.Client.DeleteAsync($"/api/indexer-proxies/{proxyId}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(await HelperAddressForAsync(app, indexer));
        Assert.Empty(flaresolverr.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/indexer-proxies/{proxyId}")).StatusCode);
    }

    [Fact]
    public async Task A_proxy_whose_helper_is_not_answering_says_so_naming_the_helper_not_the_tracker()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var proxyId = (await (await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = "http://127.0.0.1:1" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var indexer = await IndexerBehindAsync(app, proxyId);

        var helperAddress = await HelperAddressForAsync(app, indexer);
        var failure = await Assert.ThrowsAsync<CloudflareHelperUnavailableException>(
            () => app.Services.GetRequiredService<ICloudflareHelper>().FetchAsync(Tracker, CancellationToken.None, proxy: null, helperUrl: helperAddress));

        Assert.Contains("example.com is behind a Cloudflare check", failure.Message);
        Assert.Equal("http://127.0.0.1:1", failure.HelperAddress);
    }
}
