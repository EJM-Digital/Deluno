using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Engines-area register rows that touch nothing outside Deluno: each test drives the real host (or the real class) and asserts the
/// row's own outcome.
/// </summary>
public sealed class EnginesInProcessRowsTests
{
    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Row " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Theory]
    [InlineData("both")]
    [InlineData("music")]
    public async Task Trigger_refresh_with_an_unrecognised_media_type_selects_no_library_and_answers_enqueued_0(string mediaType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        await ApiScenes.CreateLibraryAsync(app, "tv");

        var response = await app.Client.PostAsJsonAsync("/api/integrations/external/trigger-refresh", new { mediaType });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enqueued").GetInt32());
        var states = await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None);
        Assert.DoesNotContain(states.Values, state => state.SearchRequested);
    }

    [Theory]
    [InlineData("/api/security/allowed-hosts")]
    [InlineData("/api/security/local-network-auth")]
    public async Task A_read_only_key_is_refused_the_security_settings_because_they_sit_in_the_system_group(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await KeyClientAsync(app, "read");

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await KeyClientAsync(app, "system")).GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task Automation_write_routes_let_a_write_key_add_and_refuse_a_read_key_with_403()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        var body = new { items = new[] { new { clientItemId = "a", mediaType = "movie", title = "Example Film", year = 2024 } } };

        var read = await (await KeyClientAsync(app, "read")).PostAsJsonAsync("/api/automation/catalogue/bulk", body);
        var write = await (await KeyClientAsync(app, "write")).PostAsJsonAsync("/api/automation/catalogue/bulk", body);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        var created = await write.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, created.GetProperty("createdCount").GetInt32());
        var id = created.GetProperty("items")[0].GetProperty("entityId").GetString();
        Assert.Equal("Example Film", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_manifest_lists_every_connection_with_its_id_name_kind_role_endpoint_as_stored_and_enabled()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.ExecuteAsync(app, "platform",
            "INSERT INTO app_connections (id, name, connection_kind, role, endpoint_url, is_enabled, created_utc, updated_utc) VALUES " +
            "('c1', 'Media server', 'mediaServer', 'library', 'HTTP://Host.Example:8096/Path/', 1, datetime('now'), datetime('now')), " +
            "('c2', 'Notifier', 'notification', 'alerts', NULL, 0, datetime('now'), datetime('now'));");

        var manifest = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/external/manifest");

        var connections = manifest.GetProperty("connections").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        Assert.Equal(2, connections.Count);
        var first = connections["c1"];
        Assert.Equal("Media server", first.GetProperty("name").GetString());
        Assert.Equal("mediaServer", first.GetProperty("connectionKind").GetString());
        Assert.Equal("library", first.GetProperty("role").GetString());
        Assert.Equal("HTTP://Host.Example:8096/Path/", first.GetProperty("endpointUrl").GetString());
        Assert.True(first.GetProperty("isEnabled").GetBoolean());
        var second = connections["c2"];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("endpointUrl").ValueKind);
        Assert.False(second.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task Browsing_a_directory_that_does_not_exist_is_404_with_the_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missing = Path.Combine(app.DataRoot, "no-such-folder");

        var response = await app.Client.GetAsync("/api/filesystem/directories?path=" + Uri.EscapeDataString(missing));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The requested directory does not exist.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public void The_default_api_key_budget_is_3000_requests_per_60_seconds()
    {
        Assert.Equal(3000, ApiRateLimitDefaults.DefaultPermitLimit);
        Assert.Equal(60, ApiRateLimitDefaults.DefaultWindowSeconds);
    }

    [Fact]
    public void Invalid_deluno_keys_are_partitioned_by_their_own_hash_never_by_the_remote_address()
    {
        string? Partition(string key, string ip)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Api-Key"] = key;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            return ApiRateLimitPartitionKeyResolver.ResolveOrExempt(context);
        }

        var first = Partition("deluno_guess-1", "203.0.113.5");
        Assert.NotNull(first);
        Assert.Equal(first, Partition("deluno_guess-1", "203.0.113.99"));
        Assert.NotEqual(first, Partition("deluno_guess-2", "203.0.113.5"));
        Assert.DoesNotContain("203.0.113", first, StringComparison.Ordinal);
    }
}
