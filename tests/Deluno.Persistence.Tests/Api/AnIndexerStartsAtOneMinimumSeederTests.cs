using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1484: a torrent indexer added through the API with a blank Minimum seeders stores 1, a value that was given is kept
/// (0 included), and Usenet, which has no seeder count, stays blank. The setup guide, the bundled trackers, Add
/// indexer and a Cardigann import all add through this same call.
/// </summary>
public sealed class AnIndexerStartsAtOneMinimumSeederTests
{
    [Theory]
    [InlineData("torznab")]
    [InlineData("rss")]
    public async Task A_torrent_indexer_added_with_a_blank_minimum_stores_1(string protocol)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await CreateAsync(app, new { protocol, baseUrl = "https://indexer.example.test/api", apiKey = "k", mediaScope = "both", isEnabled = false });

        Assert.Equal(1, saved.GetProperty("minimumSeeders").GetInt32());
        Assert.Equal(1, (await ListedAsync(app, saved.GetProperty("id").GetString()!)).GetProperty("minimumSeeders").GetInt32());
    }

    [Fact]
    public async Task A_definition_based_tracker_added_with_a_blank_minimum_stores_1()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var catalog = await app.Client.GetFromJsonAsync<JsonElement>("/api/trackers");
        var definition = catalog.EnumerateArray().First();

        var saved = await CreateAsync(app, new
        {
            protocol = "tracker",
            baseUrl = "https://tracker.example.test",
            definitionId = definition.GetProperty("id").GetString(),
            definitionSettings = new Dictionary<string, string>(),
            definitionSecrets = new Dictionary<string, string>(),
            mediaScope = "both",
            isEnabled = false
        });

        Assert.Equal(1, saved.GetProperty("minimumSeeders").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_minimum_that_was_given_is_kept_and_0_stays_0(int minimum)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await CreateAsync(app, new { protocol = "torznab", baseUrl = "https://indexer.example.test/api", apiKey = "k", mediaScope = "both", isEnabled = false, minimumSeeders = minimum });

        Assert.Equal(minimum, saved.GetProperty("minimumSeeders").GetInt32());
    }

    [Fact]
    public async Task A_usenet_indexer_added_with_a_blank_minimum_stays_blank()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await CreateAsync(app, new { protocol = "newznab", baseUrl = "https://indexer.example.test/api", apiKey = "k", mediaScope = "both", isEnabled = false });

        Assert.Equal(JsonValueKind.Null, saved.GetProperty("minimumSeeders").ValueKind);
    }

    private static async Task<JsonElement> CreateAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListedAsync(ApplicationTestHost app, string id)
    {
        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers");
        return all.EnumerateArray().First(item => item.GetProperty("id").GetString() == id);
    }
}
