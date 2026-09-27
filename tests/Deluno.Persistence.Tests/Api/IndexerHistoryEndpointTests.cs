using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #754: the combined Indexer History endpoint, wired end to end through the
/// real container -- a fresh install has nothing, and the Clear route needs
/// authentication like every other write.
/// </summary>
public sealed class IndexerHistoryEndpointTests
{
    [Fact]
    public async Task A_new_install_has_no_indexer_history()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/indexer-history");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Clearing_requires_authentication()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.CreateAnonymousClient().PostAsync("/api/indexer-history/clear", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_can_clear_the_combined_history()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/indexer-history/clear", content: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("cleared").GetBoolean());
    }
}
