using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1134: a test run in the drawer before Add is saved with the indexer, so the
/// list shows its result and time straight away instead of "Untested".
/// </summary>
public sealed class ATestRunBeforeAddingAnIndexerIsKeptTests
{
    [Fact]
    public async Task An_indexer_added_after_a_test_keeps_that_result()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var request = new CreateIndexerRequest("Tested first", "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", true)
        {
            TestedHealth = new IndexerTestedHealth("degraded", "Behind Cloudflare, no helper set.", "cloudflare", 1200)
        };
        var response = await app.Client.PostAsJsonAsync("/api/indexers", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", created.GetProperty("healthStatus").GetString());
        Assert.Equal("Behind Cloudflare, no helper set.", created.GetProperty("lastHealthMessage").GetString());
        Assert.NotEqual(JsonValueKind.Null, created.GetProperty("lastHealthTestUtc").ValueKind);
    }

    [Fact]
    public async Task An_indexer_added_without_a_test_stays_untested()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/indexers",
            new CreateIndexerRequest("Not tested", "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", true));

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("untested", created.GetProperty("healthStatus").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastHealthTestUtc").ValueKind);
    }

    [Fact]
    public async Task A_made_up_status_is_not_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var request = new CreateIndexerRequest("Odd", "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", true)
        {
            TestedHealth = new IndexerTestedHealth("fine-honest", "trust me", null, null)
        };
        var created = await (await app.Client.PostAsJsonAsync("/api/indexers", request)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("untested", created.GetProperty("healthStatus").GetString());
    }
}
