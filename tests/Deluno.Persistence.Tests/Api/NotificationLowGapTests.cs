using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Low-severity gap rows (#1329) of the notification connections, on the whole application with its own SQLite databases. The
/// receiving end, where there is one, is the recorded ntfy 2.34.0 served over a loopback port.
/// </summary>
public sealed class NotificationLowGapTests
{
    private const string Message = "Sintel (2010) was imported from Sintel.2010.WEB.1080p using copy.";

    private static Task<RecordedAppServer> OpenNtfyAsync()
        => RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? RecordedReplies.Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);

    private static async Task TurnOnAsync(ApplicationTestHost app)
        => Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);

    private static async Task<JsonElement> AddAsync(ApplicationTestHost app, object body)
    {
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", body);
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return await made.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task SayAsync(ApplicationTestHost app, string category = "import.completed")
        => app.Services.GetRequiredService<IOutboundNotificationService>().DispatchAsync(category, "Import completed: Sintel (2010)", Message, null, CancellationToken.None);

    private static async Task<JsonElement> WebhookAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Event_filters_and_tags_are_stored_trimmed_without_repeats_and_comma_joined()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var made = await AddAsync(app, new
        {
            name = "Phone", url = "https://hooks.example.test/a", eventFilters = "  grab , import,GRAB, ,import ", isEnabled = true,
            provider = "generic", tags = " kids,Kids , anime,, "
        });
        var id = made.GetProperty("id").GetString()!;

        Assert.Equal("grab, import", made.GetProperty("eventFilters").GetString());
        Assert.Equal("kids, anime", made.GetProperty("tags").GetString());
        Assert.Equal("grab, import", await ApiScenes.TextAsync(app, "platform", $"SELECT event_filters FROM notification_webhooks WHERE id = '{id}'"));
        Assert.Equal("kids, anime", await ApiScenes.TextAsync(app, "platform", $"SELECT tags FROM notification_webhooks WHERE id = '{id}'"));

        var changed = await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", new
        {
            name = "Phone", url = "https://hooks.example.test/a", eventFilters = " upgrade ,upgrade,  rename", isEnabled = true, tags = "a , A,b"
        });
        Assert.True(changed.IsSuccessStatusCode);
        var after = await WebhookAsync(app, id);
        Assert.Equal("upgrade, rename", after.GetProperty("eventFilters").GetString());
        Assert.Equal("a, b", after.GetProperty("tags").GetString());
    }

    [Fact]
    public async Task A_connection_with_a_blank_event_filter_is_told_every_event()
    {
        await using var receiver = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, new { name = "Everything", url = receiver.BaseUrl + "/all", eventFilters = "", isEnabled = true, provider = "ntfy" });

        foreach (var category in new[] { "import.completed", "rename", "health.degraded", "anything.at.all" })
        {
            await SayAsync(app, category);
        }

        Assert.Equal(4, receiver.Requests.Count);
    }

    [Fact]
    public async Task An_unknown_provider_word_is_saved_as_a_generic_webhook_and_an_address_that_is_not_http_is_accepted_unchecked()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var odd = await AddAsync(app, new { name = "Pigeon", url = "carrier-pigeon://coop-7/deluno", eventFilters = "all", isEnabled = true, provider = "carrier-pigeon" });

        Assert.Equal("generic", odd.GetProperty("provider").GetString());
        Assert.Equal("generic", await ApiScenes.TextAsync(app, "platform", $"SELECT provider FROM notification_webhooks WHERE id = '{odd.GetProperty("id").GetString()}'"));
        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).GetArrayLength());
    }

    [Fact]
    public async Task A_stored_notification_preferences_row_that_is_not_valid_JSON_reads_back_as_the_defaults()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var defaults = await app.Client.GetStringAsync("/api/notification-preferences");
        await ApiScenes.ExecuteAsync(app, "platform", "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('notifications.preferences', '{not json', '2026-10-19T00:00:00Z') ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;");

        var read = await app.Client.GetAsync("/api/notification-preferences");

        Assert.True(read.IsSuccessStatusCode);
        Assert.Equal(defaults, await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_delivery_list_shows_what_happened_but_neither_the_message_body_nor_the_address_it_went_to()
    {
        await using var receiver = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, new { name = "Phone", url = receiver.BaseUrl + "/deluno-films", eventFilters = "all", isEnabled = true, provider = "ntfy" });
        await SayAsync(app);

        var raw = await app.Client.GetStringAsync("/api/notification-webhooks/deliveries");

        var item = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray());
        var names = item.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("title", names);
        Assert.DoesNotContain(names, name => name.Contains("message", StringComparison.OrdinalIgnoreCase) || name.Contains("details", StringComparison.OrdinalIgnoreCase) || name.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Message, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(receiver.BaseUrl, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_try_success_is_delivered_with_status_200_after_one_attempt_and_stamps_the_connection_and_clears_its_old_error()
    {
        await using var receiver = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = (await AddAsync(app, new { name = "Phone", url = receiver.BaseUrl + "/deluno-films", eventFilters = "all", isEnabled = true, provider = "ntfy" })).GetProperty("id").GetString()!;
        await ApiScenes.ExecuteAsync(app, "platform", $"UPDATE notification_webhooks SET last_error = 'an old refusal' WHERE id = '{id}'");
        Assert.Equal("an old refusal", (await WebhookAsync(app, id)).GetProperty("lastError").GetString());

        await SayAsync(app);

        var delivery = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray());
        Assert.Equal("delivered", delivery.GetProperty("status").GetString());
        Assert.Equal(200, delivery.GetProperty("lastStatusCode").GetInt32());
        Assert.Equal(1, delivery.GetProperty("attemptCount").GetInt32());
        var webhook = await WebhookAsync(app, id);
        Assert.NotEqual(JsonValueKind.Null, webhook.GetProperty("lastFiredUtc").ValueKind);
        Assert.True(string.IsNullOrEmpty(webhook.GetProperty("lastError").GetString()));
    }
}
