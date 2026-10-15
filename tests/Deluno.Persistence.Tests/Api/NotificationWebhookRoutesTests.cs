using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The webhook (notification connection) routes as a person's browser uses them, on the whole application over HTTP with
/// its own databases: what a create needs, what an update keeps, delete, the test button with the master switch off, a
/// replay of a delivery that does not exist, and that none of it is open to someone who is not signed in.
/// </summary>
public sealed class NotificationWebhookRoutesTests
{
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Reads the platform database file itself, not the API, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_webhook_needs_an_address_except_Telegram_and_Pushover_which_post_to_their_own_service()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var generic = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Chat", url = "", eventFilters = "all", isEnabled = true, provider = "generic" });
        Assert.Equal(HttpStatusCode.BadRequest, generic.StatusCode);
        Assert.Contains("Webhook URL is required.", await generic.Content.ReadAsStringAsync());

        var ntfy = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Phone", url = "  ", eventFilters = "all", isEnabled = true, provider = "ntfy" });
        Assert.Equal(HttpStatusCode.BadRequest, ntfy.StatusCode);

        foreach (var provider in new[] { "telegram", "pushover" })
        {
            var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new
            {
                name = provider, url = "", eventFilters = "all", isEnabled = true, provider, configJson = "{\"botToken\":\"t\",\"chatId\":\"c\",\"appToken\":\"a\",\"userKey\":\"u\"}"
            });
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        }

        Assert.Equal(2, (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).GetArrayLength());
        Assert.Equal("2", await StoredAsync(app, "SELECT count(*) FROM notification_webhooks WHERE provider IN ('telegram','pushover')"));
        Assert.Equal("0", await StoredAsync(app, "SELECT count(*) FROM notification_webhooks WHERE provider IN ('generic','ntfy')"));
    }

    [Fact]
    public async Task A_new_webhook_keeps_its_events_tags_provider_config_and_health_flag_and_an_update_that_omits_them_changes_none_of_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", new
        {
            name = "Family", url = "https://hooks.example.test/a", eventFilters = "grab,import", isEnabled = true, provider = "slack",
            configJson = "{\"note\":\"kept\"}", tags = "kids", includeHealthWarnings = true
        }));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("slack", created.GetProperty("provider").GetString());
        Assert.Equal(["grab", "import"], created.GetProperty("eventFilters").GetString()!.Split(',', StringSplitOptions.TrimEntries));
        Assert.Equal("kids", created.GetProperty("tags").GetString());
        Assert.True(created.GetProperty("includeHealthWarnings").GetBoolean());

        // The row switch sends only what it knows; provider, config, tags and the health flag it leaves out must survive.
        var paused = await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", new { name = "Family", url = "https://hooks.example.test/a", eventFilters = "grab,import", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var after = (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.False(after.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("slack", after.GetProperty("provider").GetString());
        Assert.Equal("kids", after.GetProperty("tags").GetString());
        Assert.True(after.GetProperty("includeHealthWarnings").GetBoolean());
        Assert.Contains("kept", after.GetProperty("configJson").GetString());
        Assert.Equal("0|slack|kids|1", await StoredAsync(app, $"SELECT is_enabled || '|' || provider || '|' || tags || '|' || include_health_warnings FROM notification_webhooks WHERE id = '{id}'"));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/notification-webhooks/not-a-webhook", new { name = "x", url = "https://hooks.example.test", eventFilters = "all", isEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_webhook_removes_it_and_saying_it_twice_or_naming_one_that_never_existed_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Gone", url = "https://hooks.example.test/a", eventFilters = "all", isEnabled = true }))).GetProperty("id").GetString()!;

        Assert.Equal("1", await StoredAsync(app, "SELECT count(*) FROM notification_webhooks"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/notification-webhooks/{id}")).StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT count(*) FROM notification_webhooks"));
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/notification-webhooks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/notification-webhooks/never-existed")).StatusCode);
    }

    [Fact]
    public async Task The_test_button_says_notifications_are_paused_when_the_master_switch_is_off_and_sends_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await ReadAsync(await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Quiet", url = "https://hooks.example.test/a", eventFilters = "all", isEnabled = true }))).GetProperty("id").GetString()!;
        var off = await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = false });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync());

        var result = await ReadAsync(await app.Client.PostAsync($"/api/notification-webhooks/{id}/test", null));

        Assert.False(result.GetProperty("sent").GetBoolean());
        Assert.Equal("Notifications are paused. Turn on Send notifications to test this webhook.", result.GetProperty("message").GetString());
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).GetArrayLength());
    }

    [Fact]
    public async Task Replaying_a_delivery_that_does_not_exist_is_a_not_found_and_a_status_filter_that_is_not_one_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/notification-webhooks/deliveries/no-such-delivery/replay", null)).StatusCode);
        var badFilter = await app.Client.GetAsync("/api/notification-webhooks/deliveries?status=sideways");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        Assert.Contains("Status must be pending, retrying, delivered, or dead-letter.", await badFilter.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_add_change_test_replay_or_delete_a_webhook()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/notification-webhooks", new { name = "x", url = "https://hooks.example.test", eventFilters = "all", isEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/notification-webhooks/any", new { name = "x", url = "https://hooks.example.test", eventFilters = "all", isEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/notification-webhooks/any")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notification-webhooks/any/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notification-webhooks/deliveries/any/replay", null)).StatusCode);
    }
}
