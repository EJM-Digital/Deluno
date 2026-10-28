using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>POST /api/integrations/external/trigger-refresh</c>: an empty or broken body is a 400, a read-only key is refused
/// with a 403, and a key that holds queue or imports is let through (a library search is asked for).
/// </summary>
public sealed class TriggerRefreshAccessTests
{
    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Refresh " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("")]
    [InlineData("{bad")]
    public async Task An_empty_or_malformed_body_is_a_400_and_asks_for_no_search(string json)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/integrations/external/trigger-refresh", Body(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("An external app requested refresh", await app.Client.GetStringAsync("/api/activity"));
    }

    [Theory]
    [InlineData("read", HttpStatusCode.Forbidden)]
    [InlineData("queue", HttpStatusCode.OK)]
    [InlineData("imports", HttpStatusCode.OK)]
    public async Task The_scope_of_the_key_decides_whether_a_refresh_is_asked_for(string scopes, HttpStatusCode expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await KeyClientAsync(app, scopes);

        var response = await client.PostAsync("/api/integrations/external/trigger-refresh", Body("{}"));

        Assert.Equal(expected, response.StatusCode);
        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Equal(expected == HttpStatusCode.OK, activity.Contains("An external app requested refresh", StringComparison.Ordinal));
    }
}
