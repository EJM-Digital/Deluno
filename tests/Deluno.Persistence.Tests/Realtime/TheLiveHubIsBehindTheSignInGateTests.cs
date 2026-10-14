using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The live-update hub is behind the same sign-in gate as the rest of the API: a connection with no credential, a wrong or
/// expired token, a revoked API key or a key that cannot read is turned away when it negotiates, before any event is pushed.
/// </summary>
public sealed class TheLiveHubIsBehindTheSignInGateTests
{
    private const string Negotiate = "/hubs/deluno/negotiate?negotiateVersion=1";

    [Fact]
    public async Task A_connection_with_no_credential_is_refused_when_it_negotiates()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsync(Negotiate, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_connection_negotiates()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync(Negotiate, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_or_expired_session_token_is_refused_when_it_negotiates()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var wrong = app.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token-deluno-issued");

        using var response = await wrong.PostAsync(Negotiate, content: null);
        using var inTheQuery = await app.CreateAnonymousClient().PostAsync(Negotiate + "&access_token=not-a-token-deluno-issued", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inTheQuery.StatusCode);
    }

    [Fact]
    public async Task A_revoked_API_key_is_refused_when_it_negotiates_and_a_live_one_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Hub key", scopes = "read" });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var key = body.GetProperty("apiKey").GetString()!;
        var id = body.GetProperty("item").GetProperty("id").GetString()!;
        using var keyed = app.CreateAnonymousClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var live = await keyed.PostAsync(Negotiate, content: null);
        (await app.Client.DeleteAsync($"/api/api-keys/{id}")).EnsureSuccessStatusCode();
        using var revoked = await keyed.PostAsync(Negotiate, content: null);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task A_key_that_cannot_read_is_refused_when_it_negotiates()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Hand-off only", scopes = "imports" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var keyed = app.CreateAnonymousClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var response = await keyed.PostAsync(Negotiate, content: null);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"answered {(int)response.StatusCode}");
    }
}
