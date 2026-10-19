using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The notification webhook routes (coverage register, area api) against what a real ntfy server answered when Deluno published to it
/// (tests/fixtures/recorded/ntfy, from the published image in Docker): one server anyone may publish to and one where nobody may without an
/// account. The loopback servers below serve those recordings, so the real sender, the real retry rules, the real delivery history and the
/// real database run. A refused publish is tried three times and ends as a dead letter that says why; a replay after the webhook is mended
/// delivers it.
/// </summary>
public sealed class NotificationWebhooksOnARecordedNtfyTests
{
    private const string Topic = "/deluno-test";
    private const string TestMessage = "This is a test notification from Deluno. Your webhook is configured correctly.";

    private static Task<RecordedAppServer> NtfyAsync(string state)
        => RecordedAppServer.StartAsync("ntfy", [RecordedReplies.Ntfy(state)]);

    private static async Task TurnOnAsync(ApplicationTestHost app)
    {
        var on = await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true });
        Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> AddAsync(ApplicationTestHost app, RecordedAppServer server, string? configJson = null)
    {
        var response = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new
        {
            name = "Phone", url = server.BaseUrl + Topic, eventFilters = "all", isEnabled = true, provider = "ntfy", configJson
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> TestAsync(ApplicationTestHost app, string id)
    {
        var response = await app.Client.PostAsync($"/api/notification-webhooks/{id}/test", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_webhook_is_added_tested_against_ntfy_and_the_message_arrives_then_removed()
    {
        await using var ntfy = await NtfyAsync("publish-ok");
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);

        var webhook = await AddAsync(app, ntfy);
        var id = webhook.GetProperty("id").GetString()!;
        Assert.Equal("ntfy", webhook.GetProperty("provider").GetString());
        Assert.Equal(id, Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray()).GetProperty("id").GetString());

        var result = await TestAsync(app, id);

        Assert.True(result.GetProperty("sent").GetBoolean());
        Assert.Equal("delivered", result.GetProperty("status").GetString());
        Assert.Equal(1, result.GetProperty("attempts").GetInt32());
        var published = Assert.Single(ntfy.Requests);
        Assert.Equal("POST", published.Method);
        Assert.Equal(Topic, published.PathAndQuery);
        Assert.Equal(TestMessage, published.Body);
        Assert.Empty(ntfy.Unanswered);
        var delivery = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray());
        Assert.Equal("delivered", delivery.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/notification-webhooks/{id}")).StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/notification-webhooks/{id}/test", content: null)).StatusCode);
    }

    [Fact]
    public async Task A_publish_ntfy_refuses_is_tried_three_times_ends_as_a_dead_letter_that_says_why_and_a_replay_after_the_webhook_is_mended_delivers_it()
    {
        await using var locked = await NtfyAsync("publish-refused");
        await using var open = await NtfyAsync("publish-ok");
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = (await AddAsync(app, locked)).GetProperty("id").GetString()!;

        var failed = await TestAsync(app, id);

        Assert.False(failed.GetProperty("sent").GetBoolean());
        Assert.Equal("dead-letter", failed.GetProperty("status").GetString());
        Assert.Equal(3, failed.GetProperty("attempts").GetInt32());
        Assert.Contains("403", failed.GetProperty("error").GetString());
        Assert.Equal(3, locked.Requests.Count);
        var deliveryId = failed.GetProperty("deliveryId").GetString()!;
        Assert.Equal("dead-letter", Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray()).GetProperty("status").GetString());

        var mended = await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", new { name = "Phone", url = open.BaseUrl + Topic, eventFilters = "all", isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, mended.StatusCode);
        var mendedUrl = (await mended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.StartsWith(open.BaseUrl, mendedUrl); // #1364: the address comes back masked, never with its topic
        Assert.DoesNotContain(Topic.TrimStart('/'), mendedUrl);
        var replay = await app.Client.PostAsync($"/api/notification-webhooks/deliveries/{deliveryId}/replay", content: null);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replayed.GetProperty("sent").GetBoolean());
        Assert.Equal("delivered", replayed.GetProperty("status").GetString());
        Assert.Equal(deliveryId, replayed.GetProperty("deliveryId").GetString());
        Assert.Equal(TestMessage, Assert.Single(open.Requests).Body);
        Assert.Equal(3, locked.Requests.Count);
        Assert.Equal("delivered", Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_token_ntfy_does_not_know_is_sent_as_a_bearer_token_and_the_refusal_is_reported_and_kept_in_the_webhooks_last_error()
    {
        await using var ntfy = await NtfyAsync("publish-wrong-token");
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = (await AddAsync(app, ntfy, "{\"accessToken\":\"tk_notatokenthatexists00000000000\"}")).GetProperty("id").GetString()!;

        var result = await TestAsync(app, id);

        Assert.False(result.GetProperty("sent").GetBoolean());
        Assert.Contains("401", result.GetProperty("error").GetString());
        Assert.All(ntfy.Requests, request => Assert.Equal("Bearer tk_notatokenthatexists00000000000", request.Authorization));
        var stored = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray());
        Assert.Contains("401", stored.GetProperty("lastError").GetString());
    }
}
