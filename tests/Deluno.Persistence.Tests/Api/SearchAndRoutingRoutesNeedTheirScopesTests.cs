using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Searching, library search-now and skip-cycle, the ranking model's train and rollback, and intelligent routing: who may ask.
/// Each route is called with no sign-in, with a read-only key and with a write key, through the real host.
/// </summary>
public sealed class SearchAndRoutingRoutesNeedTheirScopesTests
{
    public static TheoryData<string, string, string> WriteRoutes => new()
    {
        { "POST", "/api/search", """{"query":"Example Film","mediaType":"movies"}""" },
        { "POST", "/api/libraries/no-such-library/search-now", "{}" },
        { "POST", "/api/libraries/no-such-library/skip-cycle", "{}" },
        { "POST", "/api/ranking-model/train", "{}" },
        { "POST", "/api/ranking-model/rollback", """{"version":"no-such-version"}""" },
        { "POST", "/api/intelligent-routing/recommend-release", """{"releaseName":"Example.Film.2024.1080p","indexerName":"Example","seeders":10,"sizeBytes":1000000000}""" }
    };

    public static TheoryData<string> ReadRoutes => new()
    {
        "/api/intelligent-routing/snapshot",
        "/api/intelligent-routing/anomalies",
        "/api/ranking-model/status"
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_route_refuses_no_sign_in_and_a_read_only_key_and_lets_a_write_key_reach_it(string method, string path, string body)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");
        using var writer = await KeyClientAsync(app, "write");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, method, path, body)).StatusCode);
        var allowed = (await SendAsync(writer, method, path, body)).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task A_read_route_refuses_no_sign_in_and_answers_a_read_only_key(string path)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(readOnly, "GET", path, null)).StatusCode);
    }

    [Fact]
    public async Task Search_now_on_a_known_library_answers_202_flags_a_pass_and_writes_it_in_plain_words_to_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "libraries", "films");
        Directory.CreateDirectory(root);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var response = await app.Client.PostAsync($"/api/libraries/{libraryId}/search-now", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var states = await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None);
        Assert.True(states[libraryId].SearchRequested);
        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains("Deluno will check Films on the next pass", activity);
    }

    [Fact]
    public async Task Search_now_on_a_library_that_does_not_exist_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/libraries/no-such-library/search-now", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Scope " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
