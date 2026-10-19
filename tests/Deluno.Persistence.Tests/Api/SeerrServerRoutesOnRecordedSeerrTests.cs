using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Seerr connection routes (/api/seerr-servers) on the whole application, with the Seerr that Deluno tests being Seerr 3.5.0's own
/// recorded replies served over a real loopback port (tests/fixtures/recorded/seerr, #1237): saving and listing a connection, the key and
/// the webhook secret that are never handed back, the address and key checks, and what each kind of test says.
/// </summary>
public sealed class SeerrServerRoutesOnRecordedSeerrTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Recording Seerr(string state) => RecordedReplies.Seerr(state);

    /// <summary>A Seerr that answers its public status page and, for a key it knows, its settings, as the real one did.</summary>
    private static Task<RecordedAppServer> RealSeerrAsync()
        => RecordedAppServer.StartAsync("seerr", [], RecordedAppServer.Chain(
            RecordedAppServer.Answering("GET", "/api/v1/status", Seerr("status").Exchanges[0]),
            RecordedAppServer.Answering("GET", "/api/v1/settings/main", Seerr("settings-main").Single)));

    private static Task<HttpResponseMessage> SaveAsync(ApplicationTestHost app, object body) => app.Client.PostAsJsonAsync("/api/seerr-servers", body);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task The_list_never_hands_back_the_key_or_the_webhook_secret_and_only_says_whether_each_is_held()
    {
        await using var seerr = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var saved = await JsonOf(await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true }));
        var id = saved.GetProperty("id").GetString()!;
        var secret = (await JsonOf(await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", null))).GetProperty("secret").GetString()!;

        var listed = await app.Client.GetStringAsync("/api/seerr-servers");

        Assert.DoesNotContain(Key, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
        var entry = JsonDocument.Parse(listed).RootElement.EnumerateArray().Single();
        Assert.True(entry.GetProperty("hasApiKey").GetBoolean());
        Assert.True(entry.GetProperty("hasWebhookSecret").GetBoolean());
        Assert.False(entry.TryGetProperty("apiKey", out _));
        Assert.False(entry.TryGetProperty("webhookSecret", out _));
        Assert.DoesNotContain(Key, await (await app.Client.GetAsync($"/api/seerr-servers/{id}/acquisitions")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_and_the_webhook_secret_are_stored_protected_and_the_secret_comes_back_once_and_is_replaced_by_the_next()
    {
        await using var seerr = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await JsonOf(await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true }))).GetProperty("id").GetString()!;

        var first = (await JsonOf(await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", null))).GetProperty("secret").GetString()!;
        var second = (await JsonOf(await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", null))).GetProperty("secret").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
        var storedKey = await ApiScenes.TextAsync(app, "platform", "SELECT api_key FROM seerr_servers");
        var storedSecret = await ApiScenes.TextAsync(app, "platform", "SELECT webhook_secret FROM seerr_servers");
        Assert.NotEqual(Key, storedKey);
        Assert.False(string.IsNullOrWhiteSpace(storedKey));
        Assert.NotEqual(second, storedSecret);
        Assert.NotEqual(first, storedSecret);
        // The stored secret still opens: a call carrying the second secret is let through to the body, the first is not.
        using var anonymous = app.CreateAnonymousClient();
        foreach (var (secret, expected) in new[] { (first, HttpStatusCode.Unauthorized), (second, HttpStatusCode.OK) })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{id}/webhook") { Content = new StringContent(Seerr("webhook-test").Single.Request.Body!, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", secret);
            Assert.Equal(expected, (await anonymous.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task A_secret_asked_for_a_connection_that_does_not_exist_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/seerr-servers/not-a-connection/webhook-secret", null)).StatusCode);
    }

    [Fact]
    public async Task An_address_that_is_not_http_or_https_and_a_missing_key_are_each_a_400_on_their_own_field()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var badAddress = await SaveAsync(app, new { name = "Seerr", baseUrl = "ftp://192.168.1.20:5055", apiKey = Key, isEnabled = true });
        var noKey = await SaveAsync(app, new { name = "Seerr", baseUrl = "http://192.168.1.20:5055", apiKey = "", isEnabled = true });
        var noAddress = await SaveAsync(app, new { name = "Seerr", baseUrl = "", apiKey = Key, isEnabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Equal("Use the server's http or https address, for example http://192.168.1.20:5055.", (await JsonOf(badAddress)).GetProperty("errors").GetProperty("baseUrl")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("Paste the API key from Seerr's Settings → General.", (await JsonOf(noKey)).GetProperty("errors").GetProperty("apiKey")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noAddress.StatusCode);
        Assert.Empty(JsonDocument.Parse(await app.Client.GetStringAsync("/api/seerr-servers")).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Saving_again_changes_what_is_sent_keeps_the_key_when_it_is_left_blank_and_refuses_what_it_refused_on_add()
    {
        await using var seerr = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await JsonOf(await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true }))).GetProperty("id").GetString()!;

        var renamed = await app.Client.PutAsJsonAsync($"/api/seerr-servers/{id}", new { name = "Seerr", baseUrl = seerr.BaseUrl + "/", apiKey = "", isEnabled = false, tags = "requested,seerr" });
        var badAddress = await app.Client.PutAsJsonAsync($"/api/seerr-servers/{id}", new { name = "Seerr", baseUrl = "not an address", apiKey = "", isEnabled = true });
        var unknown = await app.Client.PutAsJsonAsync("/api/seerr-servers/not-a-connection", new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var saved = await JsonOf(renamed);
        Assert.Equal(seerr.BaseUrl, saved.GetProperty("baseUrl").GetString());
        Assert.False(saved.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("requested, seerr", saved.GetProperty("tags").GetString());
        Assert.True(saved.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        // The blank key kept the stored one: a test of the saved connection still gets in.
        var test = await JsonOf(await app.Client.PostAsJsonAsync($"/api/seerr-servers/{id}/test", (object?)null));
        Assert.True(test.GetProperty("ok").GetBoolean(), test.ToString());
        Assert.Equal(Key, seerr.Requests.Last().Header("x-api-key"));
    }

    [Fact]
    public async Task A_connection_is_removed_with_a_204_and_one_that_is_not_there_is_a_404()
    {
        await using var seerr = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await JsonOf(await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true }))).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/seerr-servers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/seerr-servers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/seerr-servers/not-a-connection")).StatusCode);
        Assert.Empty(JsonDocument.Parse(await app.Client.GetStringAsync("/api/seerr-servers")).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task A_draft_is_checked_for_its_fields_first_and_then_tested_with_the_key_being_offered_not_a_stored_one()
    {
        await using var seerr = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true });
        var refused = await app.Client.PostAsJsonAsync("/api/seerr-servers/test", new { name = "Seerr", baseUrl = "nope", apiKey = "", isEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var errors = (await JsonOf(refused)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("baseUrl", out _) && errors.TryGetProperty("apiKey", out _));
        var asked = seerr.Requests.Count;
        Assert.Equal(0, asked);

        var tested = await JsonOf(await app.Client.PostAsJsonAsync("/api/seerr-servers/test", new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = "the-key-on-the-form", isEnabled = true }));

        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.ToString());
        Assert.Equal("3.5.0", tested.GetProperty("version").GetString());
        Assert.All(seerr.Requests, request => Assert.Equal("the-key-on-the-form", request.Header("x-api-key")));
        Assert.Contains(seerr.Requests, request => request.PathAndQuery == "/api/v1/settings/main");
    }

    [Fact]
    public async Task Testing_a_saved_connection_records_its_health_the_version_and_the_check_and_a_body_changes_the_address_and_key_for_that_test_only()
    {
        await using var seerr = await RealSeerrAsync();
        await using var elsewhere = await RealSeerrAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await JsonOf(await SaveAsync(app, new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = Key, isEnabled = true }))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/seerr-servers/not-a-connection/test", (object?)null)).StatusCode);

        var tested = await JsonOf(await app.Client.PostAsJsonAsync($"/api/seerr-servers/{id}/test", (object?)null));

        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.ToString());
        var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/seerr-servers")).RootElement.EnumerateArray().Single();
        Assert.Equal("healthy", listed.GetProperty("healthStatus").GetString());
        Assert.Equal("3.5.0", listed.GetProperty("version").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT ok FROM connection_checks WHERE connection_id = '{id}'"));
        Assert.Equal("3.5.0", await ApiScenes.TextAsync(app, "jobs", $"SELECT version FROM connection_versions WHERE connection_id = '{id}'"));

        var other = await JsonOf(await app.Client.PostAsJsonAsync($"/api/seerr-servers/{id}/test", new { name = "Seerr", baseUrl = elsewhere.BaseUrl, apiKey = "another-key", isEnabled = true }));

        Assert.True(other.GetProperty("ok").GetBoolean(), other.ToString());
        Assert.All(elsewhere.Requests, request => Assert.Equal("another-key", request.Header("x-api-key")));
        var after = JsonDocument.Parse(await app.Client.GetStringAsync("/api/seerr-servers")).RootElement.EnumerateArray().Single();
        Assert.Equal(seerr.BaseUrl, after.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task An_address_that_answers_but_not_as_Seerr_is_degraded_and_one_that_does_not_answer_is_unreachable()
    {
        // Something that answers the status address with a page that has no version in it: Seerr's own status reply with its version taken out.
        await using var notSeerr = await RecordedAppServer.StartAsync("seerr", [], (request, _) => request.RequestUri!.AbsolutePath == "/api/v1/status" ? RecordedReplies.Json(Seerr("status").Exchanges[0].Reply.BodyWith(("version", null))) : null);
        await using var app = await ApplicationTestHost.StartAsync();
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var degraded = await JsonOf(await app.Client.PostAsJsonAsync("/api/seerr-servers/test", new { name = "Seerr", baseUrl = notSeerr.BaseUrl, apiKey = Key, isEnabled = true }));
        var unreachable = await JsonOf(await app.Client.PostAsJsonAsync("/api/seerr-servers/test", new { name = "Seerr", baseUrl = $"http://127.0.0.1:{deadPort}", apiKey = Key, isEnabled = true }));

        Assert.False(degraded.GetProperty("ok").GetBoolean());
        Assert.Equal("degraded", degraded.GetProperty("status").GetString());
        Assert.False(unreachable.GetProperty("ok").GetBoolean());
        Assert.Equal("unreachable", unreachable.GetProperty("status").GetString());
    }
}
