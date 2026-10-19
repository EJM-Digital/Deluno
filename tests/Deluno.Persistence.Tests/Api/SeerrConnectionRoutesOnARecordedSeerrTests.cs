using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Seerr connection routes (coverage register, area api) against what a real Seerr 3.5.0 answered (tests/fixtures/recorded/seerr):
/// the app Deluno asks is a loopback server serving those recordings, so the real client, the real routes and the real database run.
/// </summary>
public sealed class SeerrConnectionRoutesOnARecordedSeerrTests
{
    private static Task<RecordedAppServer> SeerrAsync(params string[] states)
        => RecordedAppServer.StartAsync("seerr", states.Select(RecordedReplies.Seerr));

    private static object Form(RecordedAppServer seerr, string name = "Lab Seerr", string apiKey = "key-1")
        => new { name, baseUrl = seerr.BaseUrl, apiKey, isEnabled = true };

    private static async Task<JsonElement> AddAsync(ApplicationTestHost app, object form)
    {
        var response = await app.Client.PostAsJsonAsync("/api/seerr-servers", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListAsync(ApplicationTestHost app)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/seerr-servers");

    [Fact]
    public async Task Testing_values_that_are_not_saved_yet_asks_the_real_seerr_and_reports_its_name_and_version_without_saving()
    {
        await using var seerr = await SeerrAsync("status", "settings-main");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/seerr-servers/test", Form(seerr));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("3.5.0", result.GetProperty("version").GetString());
        Assert.Equal("Connected to Seerr (version 3.5.0).", result.GetProperty("message").GetString());
        Assert.Contains(seerr.Requests, request => request.PathAndQuery == "/api/v1/settings/main");
        Assert.Empty(seerr.Unanswered);
        Assert.Equal(0, (await ListAsync(app)).GetArrayLength());
    }

    [Fact]
    public async Task Testing_a_key_seerr_refuses_says_the_connection_is_not_healthy_and_a_form_without_a_key_is_sent_back_unasked()
    {
        await using var seerr = await SeerrAsync("status", "wrong-key");
        await using var app = await ApplicationTestHost.StartAsync();

        var refused = await (await app.Client.PostAsJsonAsync("/api/seerr-servers/test", Form(seerr, apiKey: "wrong"))).Content.ReadFromJsonAsync<JsonElement>();
        var asked = seerr.Requests.Count;
        var blank = await app.Client.PostAsJsonAsync("/api/seerr-servers/test", new { baseUrl = seerr.BaseUrl, apiKey = "", isEnabled = true });

        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("degraded", refused.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Paste the API key", await blank.Content.ReadAsStringAsync());
        Assert.Equal(asked, seerr.Requests.Count);
    }

    [Fact]
    public async Task A_connection_is_added_edited_tested_and_removed_and_the_list_never_carries_its_key()
    {
        await using var seerr = await SeerrAsync("status", "settings-main");
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await AddAsync(app, Form(seerr, "Lab Seerr", "the-secret-key"));
        var id = created.GetProperty("id").GetString()!;
        // A connection is named from its address, never a typed name (ConnectionsNeverAskForANameTests).
        Assert.Equal($"Seerr on {Environment.MachineName}", created.GetProperty("name").GetString());
        Assert.DoesNotContain("the-secret-key", await app.Client.GetStringAsync("/api/seerr-servers"));
        Assert.Equal(id, Assert.Single((await ListAsync(app)).EnumerateArray()).GetProperty("id").GetString());

        var switchedOff = await app.Client.PutAsJsonAsync($"/api/seerr-servers/{id}", new { baseUrl = seerr.BaseUrl, apiKey = "", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, switchedOff.StatusCode);
        Assert.False(Assert.Single((await ListAsync(app)).EnumerateArray()).GetProperty("isEnabled").GetBoolean());

        var tested = await app.Client.PostAsync($"/api/seerr-servers/{id}/test", content: null);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.True((await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.Equal("healthy", Assert.Single((await ListAsync(app)).EnumerateArray()).GetProperty("healthStatus").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/seerr-servers/{id}")).StatusCode);
        Assert.Equal(0, (await ListAsync(app)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/seerr-servers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/seerr-servers/{id}/test", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/seerr-servers/{id}", Form(seerr))).StatusCode);
    }

    [Fact]
    public async Task A_new_webhook_secret_is_the_only_one_Seerrs_own_test_notification_is_accepted_with()
    {
        await using var seerr = await SeerrAsync("status", "settings-main");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await AddAsync(app, Form(seerr))).GetProperty("id").GetString()!;
        var sent = RecordedReplies.Seerr("webhook-test").Exchanges[0];
        var anonymous = app.CreateAnonymousClient();

        HttpRequestMessage Post(string? secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{id}/webhook")
            {
                Content = new StringContent(sent.Request.Body!, System.Text.Encoding.UTF8, "application/json")
            };
            if (secret is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", secret);
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Post(null))).StatusCode);

        var first = (await (await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Post(first))).StatusCode);

        var second = (await (await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Post(first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Post(second))).StatusCode);
        Assert.DoesNotContain(second, await app.Client.GetStringAsync("/api/seerr-servers"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/seerr-servers/no-such-server/webhook-secret", content: null)).StatusCode);
    }
}
