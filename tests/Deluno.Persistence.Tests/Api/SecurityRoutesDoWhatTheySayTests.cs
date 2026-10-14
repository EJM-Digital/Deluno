using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The security routes when the request is right (coverage register, area api): what each one answers and
/// what it changes. The refusals are proved by the route walk; this is the success path of each.
/// </summary>
public sealed class SecurityRoutesDoWhatTheySayTests
{
    private const string OwnerPassword = "Deluno-Test-2026!";

    /// <summary>A number straight out of the host's real SQLite file, so a test sees what is stored and not only what is answered.</summary>
    private static async Task<long> ScalarAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_creates_the_first_account_once_then_refuses_with_a_conflict()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM api_keys; DELETE FROM users;";
            await command.ExecuteNonQueryAsync();
        }

        var anonymous = app.CreateAnonymousClient();
        var status = await anonymous.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status");
        Assert.True(status!.RequiresSetup);

        var created = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "First Person", "a-long-password-1"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var login = await created.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(login!.AccessToken));
        Assert.Equal("first", login.User.Username);
        Assert.Equal("First Person", login.User.DisplayName);

        var after = await anonymous.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status");
        Assert.False(after!.RequiresSetup);

        var second = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("second", "Second", "a-long-password-2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already been configured", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_with_the_right_password_returns_a_token_that_opens_the_api_and_a_wrong_one_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var good = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var login = await good.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal("owner", login!.User.Username);
        Assert.True(login.ExpiresUtc > DateTimeOffset.UtcNow);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/tags")).StatusCode);

        var bad = await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "not-the-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_token_so_the_same_token_stops_working()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();
        var login = await (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword)))
            .Content.ReadFromJsonAsync<LoginResponse>();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/tags")).StatusCode);

        var logout = await anonymous.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tags")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_swaps_which_password_signs_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var changed = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, "A-brand-new-pass-9"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "A-brand-new-pass-9"))).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_with_a_wrong_current_one_says_so_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest("wrong-current", "A-brand-new-pass-9"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Current password is not correct.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).StatusCode);
    }

    [Fact]
    public async Task An_API_key_is_created_shown_once_listed_without_its_secret_and_revoked()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var create = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Home Assistant box", "dashboard-read"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        Assert.False(string.IsNullOrWhiteSpace(created!.ApiKey));
        Assert.Equal("Home Assistant box", created.Item.Name);
        // A template id reaches the repository as the scopes it stands for.
        Assert.Equal("read", created.Item.Scopes);

        var listJson = await app.Client.GetStringAsync("/api/api-keys");
        using (var document = JsonDocument.Parse(listJson))
        {
            var item = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(created.Item.Id, item.GetProperty("id").GetString());
        }
        Assert.DoesNotContain(created.ApiKey, listJson, StringComparison.Ordinal);

        // The key works for what its scope grants.
        var keyClient = app.CreateAnonymousClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", created.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/tags")).StatusCode);

        Assert.Equal(1, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM api_keys;"));
        var revoke = await app.Client.DeleteAsync($"/api/api-keys/{created.Item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(0, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM api_keys;"));
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/api-keys")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/api-keys/{created.Item.Id}")).StatusCode);
    }

    [Fact]
    public async Task Creating_an_API_key_without_a_name_is_refused_with_the_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("  ", "read"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Give this API key a clear name.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/api-keys")).Replace(" ", ""));
    }

    [Fact]
    public async Task The_scope_templates_list_every_preset_a_new_key_can_start_from()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var templates = await app.Client.GetFromJsonAsync<JsonElement>("/api/api-keys/scope-templates");

        var ids = templates.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToArray();
        Assert.Contains("dashboard-read", ids);
        Assert.Contains("media-automation", ids);
        Assert.Contains("full-local", ids);
    }

    [Fact]
    public async Task Allowed_hosts_save_and_read_back_trimmed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var before = await app.Client.GetFromJsonAsync<AllowedHostsOverview>("/api/security/allowed-hosts");
        Assert.False(before!.Enabled);

        var saved = await app.Client.PutAsJsonAsync("/api/security/allowed-hosts", new UpdateAllowedHostsRequest(true, [" deluno.example.com ", ""]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await app.Client.GetFromJsonAsync<AllowedHostsOverview>("/api/security/allowed-hosts");
        Assert.True(after!.Enabled);
        Assert.Equal(["deluno.example.com"], after.AllowedHosts);
    }

    [Fact]
    public async Task Allowed_hosts_refuse_a_list_that_leaves_out_the_address_in_use()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts")
        {
            Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["other.example.com"]))
        };
        request.Headers.Host = "deluno.lan";

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lock you out", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var overview = await app.Client.GetFromJsonAsync<AllowedHostsOverview>("/api/security/allowed-hosts");
        Assert.False(overview!.Enabled);
    }

    [Fact]
    public async Task Local_network_sign_in_skipping_saves_trims_and_reads_back_and_a_bad_address_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var before = await app.Client.GetFromJsonAsync<LocalNetworkAuthOverview>("/api/security/local-network-auth");
        Assert.False(before!.Enabled);

        var bad = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new UpdateLocalNetworkAuthRequest(true, ["not-an-address"], [], []));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not a valid address or subnet", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False((await app.Client.GetFromJsonAsync<LocalNetworkAuthOverview>("/api/security/local-network-auth"))!.Enabled);

        var saved = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new UpdateLocalNetworkAuthRequest(true, [" 10.20.0.0/16 ", "10.20.0.0/16"], ["192.168.1.20"], []));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await app.Client.GetFromJsonAsync<LocalNetworkAuthOverview>("/api/security/local-network-auth");
        Assert.True(after!.Enabled);
        Assert.Equal(["10.20.0.0/16"], after.AdditionalLocalNetworks);
        Assert.Equal(["192.168.1.20"], after.AllowList);
    }
}
