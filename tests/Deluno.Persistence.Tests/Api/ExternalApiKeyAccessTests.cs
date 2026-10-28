using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Who may read the external-integration routes with an API key: the Bearer form, a read key, and the refusals
/// for no key and for a key that holds only the imports scope (docs/external-integration-api.md, "API key scopes").
/// </summary>
public sealed class ExternalApiKeyAccessTests
{
    private static async Task<(HttpClient Client, string Key)> KeyAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Access " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return (client, key);
    }

    [Fact]
    public async Task A_key_sent_as_a_bearer_token_is_accepted_and_a_wrong_deluno_bearer_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, key) = await KeyAsync(app, "read");

        var good = app.CreateAnonymousClient();
        good.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var wrong = app.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "deluno_" + new string('a', 40));

        var accepted = await good.GetAsync("/api/system/status");
        var refused = await wrong.GetAsync("/api/system/status");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Deluno", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appName").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Theory]
    [InlineData("/api/integrations/external/manifest")]
    [InlineData("/api/integrations/external/health")]
    [InlineData("/api/integrations/external/queue")]
    [InlineData("/api/integrations/external/activity")]
    public async Task The_manifest_health_queue_and_activity_are_open_to_a_read_key_and_closed_to_no_key_and_to_an_imports_key(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (read, _) = await KeyAsync(app, "read");
        var (importsOnly, _) = await KeyAsync(app, "imports");
        var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await importsOnly.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData("/api/security/allowed-hosts")]
    [InlineData("/api/security/local-network-auth")]
    public async Task The_security_settings_need_the_system_scope_so_a_read_key_is_refused_a_system_key_reads_them_and_no_key_is_turned_away(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (read, _) = await KeyAsync(app, "read");
        var (system, _) = await KeyAsync(app, "system");

        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await system.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync(route)).StatusCode);
    }
}
