using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1395: a name sent when creating or renaming a notification webhook or an import list is kept as sent (trimmed);
/// only a missing or blank name falls back to the name Deluno derives from the app and address.
/// </summary>
public sealed class ANameSentThroughTheApiIsKeptTests
{
    [Fact]
    public async Task A_webhook_is_created_and_renamed_with_the_name_sent_and_a_missing_name_is_derived()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var named = await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", Hook("  Movie night  ", "https://hooks.example.test/a")));
        Assert.Equal("Movie night", named.GetProperty("name").GetString());
        var id = named.GetProperty("id").GetString();

        var renamed = await ReadAsync(await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", Hook(" Date night ", "https://hooks.example.test/a")));
        Assert.Equal("Date night", renamed.GetProperty("name").GetString());
        Assert.Equal("Date night", NameOf(await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks"), id!));

        var derived = await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", Hook(null, "https://hooks.example.test/b")));
        Assert.Equal("Discord on hooks.example.test", derived.GetProperty("name").GetString());
        var blank = await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", Hook("   ", "https://hooks.example.test/c")));
        Assert.StartsWith("Discord on hooks.example.test", blank.GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.NotEqual("   ", blank.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_import_list_is_created_and_renamed_with_the_name_sent_and_a_missing_name_is_derived()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var named = await ReadAsync(await app.Client.PostAsJsonAsync("/api/intake-sources", List("  Friday picks ", "https://lists.example.test/a.xml")));
        Assert.Equal("Friday picks", named.GetProperty("name").GetString());
        var id = named.GetProperty("id").GetString();

        var renamed = await ReadAsync(await app.Client.PutAsJsonAsync($"/api/intake-sources/{id}", List(" Saturday picks", "https://lists.example.test/a.xml")));
        Assert.Equal("Saturday picks", renamed.GetProperty("name").GetString());
        Assert.Equal("Saturday picks", NameOf(await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources"), id!));

        var derived = await ReadAsync(await app.Client.PostAsJsonAsync("/api/intake-sources", List(null, "https://lists.example.test/b.xml")));
        Assert.Equal("Rss: b.xml", derived.GetProperty("name").GetString());
        Assert.NotEqual("Friday picks", derived.GetProperty("name").GetString());
    }

    private static object Hook(string? name, string url)
        => new { name, url, eventFilters = "health", isEnabled = false, provider = "discord" };

    private static object List(string? name, string feedUrl)
        => new { name, provider = "rss", feedUrl, mediaType = "movies", isEnabled = false };

    private static string? NameOf(JsonElement all, string id)
        => all.EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).GetProperty("name").GetString();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
