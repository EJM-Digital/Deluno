using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1248: a name given to an indexer through the API is kept, for a Torznab, a Newznab and a definition-based
/// indexer alike. The address's host names an indexer only when no name is given.
/// </summary>
public sealed class AnIndexerKeepsTheNameItIsGivenTests
{
    [Theory]
    [InlineData("torznab")]
    [InlineData("newznab")]
    public async Task A_name_given_to_an_address_based_indexer_is_kept_through_the_api(string protocol)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await CreateAsync(app, new
        {
            name = "My favourite " + protocol,
            protocol,
            baseUrl = "https://indexer.example.test/api",
            apiKey = "k",
            mediaScope = "both",
            isEnabled = false
        });

        Assert.Equal("My favourite " + protocol, saved.GetProperty("name").GetString());
        Assert.Equal("My favourite " + protocol, await ListedNameAsync(app, saved.GetProperty("id").GetString()!));
    }

    [Theory]
    [InlineData("torznab")]
    [InlineData("newznab")]
    public async Task An_address_based_indexer_saved_without_a_name_is_named_after_its_host(string protocol)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await CreateAsync(app, new { protocol, baseUrl = "https://www.indexer.example.test/api", apiKey = "k", mediaScope = "both", isEnabled = false });

        Assert.Equal("indexer.example.test", saved.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_name_given_to_a_definition_based_indexer_is_kept_and_without_one_it_is_named_after_its_definition()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var catalog = await app.Client.GetFromJsonAsync<JsonElement>("/api/trackers");
        var definition = catalog.EnumerateArray().First();
        var baseUrl = definition.GetProperty("links").EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
            ? definition.GetProperty("links")[0].GetString()!
            : "https://tracker.example.test";

        object Body(string? name) => new
        {
            name,
            protocol = "tracker",
            baseUrl,
            definitionId = definition.GetProperty("id").GetString(),
            definitionSettings = new Dictionary<string, string>(),
            definitionSecrets = new Dictionary<string, string>(),
            mediaScope = "both",
            isEnabled = false
        };

        var named = await CreateAsync(app, Body("My private tracker"));
        var unnamed = await CreateAsync(app, Body(null));

        Assert.Equal("My private tracker", named.GetProperty("name").GetString());
        Assert.NotEqual("My private tracker", unnamed.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(unnamed.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_name_given_in_a_later_save_through_the_api_renames_the_indexer_and_is_kept_by_the_next_save()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var saved = await CreateAsync(app, new { protocol = "torznab", baseUrl = "https://indexer.example.test/api", apiKey = "k", mediaScope = "both", isEnabled = false });
        var id = saved.GetProperty("id").GetString()!;

        var renamed = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { name = "Renamed" });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        Assert.Equal("Renamed", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        var later = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { priority = 30 });
        Assert.Equal("Renamed", (await later.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    private static async Task<JsonElement> CreateAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> ListedNameAsync(ApplicationTestHost app, string id)
    {
        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers");
        return all.EnumerateArray().First(item => item.GetProperty("id").GetString() == id).GetProperty("name").GetString();
    }
}
