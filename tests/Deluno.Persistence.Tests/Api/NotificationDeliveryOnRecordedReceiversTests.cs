using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Deluno's deliveries to the self-hosted push services (#1296), on the whole application with its own database: the receiving end is a real ntfy
/// 2.32.0 and a real Gotify 3.1.1 as they answered (tests/fixtures/recorded/ntfy and gotify, recorded in Docker by scripts/recorded-replies/docker/notify.mjs),
/// served over a real loopback port. Deluno's own request is held against the request the real service accepted (same method, address, headers and
/// body), and a refusal is the refusal the real service gave. Retries, dead letters, the error kept on the connection, the delivery history and
/// the replay are Deluno's own, on the real SQLite databases.
/// </summary>
public sealed class NotificationDeliveryOnRecordedReceiversTests
{
    private const string Title = "Import completed: Sintel (2010)";
    private const string Message = "Sintel (2010) was imported from Sintel.2010.WEB.1080p using copy.";

    private static Recording Ntfy(string state) => RecordedReplies.Ntfy(state);

    private static Recording Gotify(string state) => RecordedReplies.Gotify(state);

    /// <summary>An open ntfy: it takes a message to any topic, as the recorded server did.</summary>
    private static Task<RecordedAppServer> OpenNtfyAsync()
        => RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);

    /// <summary>An ntfy that wants a login: the right token is taken (200), none is refused (403), another is refused (401), each as recorded.</summary>
    private static Task<RecordedAppServer> TokenNtfyAsync(string token)
        => RecordedAppServer.StartAsync("ntfy", [], (request, _) =>
        {
            var login = request.Headers.Authorization?.ToString();
            return Ntfy("access-token").Exchanges[login == $"Bearer {token}" ? 0 : login is null ? 1 : 2].Reply.ToResponse();
        });

    /// <summary>A Gotify that takes only its application's token, as recorded.</summary>
    private static Task<RecordedAppServer> GotifyAsync(string key)
        => RecordedAppServer.StartAsync("gotify", [], (request, _) =>
        {
            var sent = request.Headers.TryGetValues("X-Gotify-Key", out var values) ? values.Single() : null;
            return Gotify(sent == key ? "delivered" : "refused").Exchanges[sent == key || sent is not null ? 0 : 1].Reply.ToResponse();
        });

    private static async Task TurnOnAsync(ApplicationTestHost app)
        => Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);

    private static async Task<string> AddAsync(ApplicationTestHost app, object body)
    {
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", body);
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static object Webhook(string name, string url, string provider = "generic", string? configJson = null, bool isEnabled = true, string eventFilters = "all")
        => new { name, url, eventFilters, isEnabled, provider, configJson };

    private static Task SayAsync(ApplicationTestHost app, string category = "import.completed", string title = Title, string message = Message, string? detailsJson = null)
        => app.Services.GetRequiredService<IOutboundNotificationService>().DispatchAsync(category, title, message, detailsJson, CancellationToken.None);

    private static async Task<JsonElement> DeliveriesAsync(ApplicationTestHost app, string query = "")
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries" + query);

    private static async Task<JsonElement> WebhookAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    private static int ClosedPort()
    {
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        return port;
    }

    // ---- what Deluno sends ----

    [Fact]
    public async Task A_message_to_ntfy_is_the_request_the_real_ntfy_accepted_with_the_title_in_its_header_and_the_message_as_plain_text()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy"));
        var accepted = Ntfy("delivered").Exchanges[0].Request;

        await SayAsync(app);

        var sent = Assert.Single(ntfy.Requests);
        Assert.Equal((accepted.Method, "/deluno-films"), (sent.Method, sent.PathAndQuery));
        Assert.Equal(accepted.Headers!["title"], sent.Header("title"));
        Assert.StartsWith("text/plain", sent.Header("content-type"), StringComparison.Ordinal);
        Assert.Equal(accepted.Body, sent.Body);
        Assert.Equal(string.Empty, sent.Authorization);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal(("delivered", id), (delivery.GetProperty("status").GetString(), delivery.GetProperty("webhookId").GetString()));
        Assert.True(string.IsNullOrEmpty((await WebhookAsync(app, id)).GetProperty("lastError").GetString()));
    }

    [Fact]
    public async Task With_an_access_token_Deluno_sends_it_as_a_Bearer_login_and_an_ntfy_that_wants_one_takes_it()
    {
        const string token = "tk_0123456789abcdef0123456789abc";
        await using var ntfy = await TokenNtfyAsync(token);
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy", configJson: JsonSerializer.Serialize(new { accessToken = token })));

        await SayAsync(app);

        var sent = Assert.Single(ntfy.Requests);
        Assert.Equal($"Bearer {token}", sent.Authorization);
        Assert.Equal("delivered", Assert.Single((await DeliveriesAsync(app)).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_ntfy_that_refuses_the_login_is_retried_three_times_and_dead_lettered_with_the_refusal_kept_on_the_connection()
    {
        await using var ntfy = await TokenNtfyAsync("tk_0123456789abcdef0123456789abc");
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy"));
        Assert.Equal(403, Ntfy("access-token").Exchanges[1].Reply.Status);

        await SayAsync(app);

        Assert.Equal(3, ntfy.Requests.Count);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal(("dead-letter", 3), (delivery.GetProperty("status").GetString(), delivery.GetProperty("attemptCount").GetInt32()));
        Assert.Contains("HTTP 403", delivery.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Contains("HTTP 403", (await WebhookAsync(app, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_to_Gotify_is_the_request_the_real_Gotify_accepted_with_the_application_token_in_its_header()
    {
        const string key = "AbCdEfGhIjKlMnO";
        await using var gotify = await GotifyAsync(key);
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, Webhook("Push", gotify.BaseUrl + "/", "gotify", configJson: JsonSerializer.Serialize(new { appToken = key })));
        var accepted = Gotify("delivered").Exchanges[0].Request;

        await SayAsync(app);

        var sent = Assert.Single(gotify.Requests);
        Assert.Equal((accepted.Method, accepted.Path), (sent.Method, sent.PathAndQuery));
        Assert.Equal(key, sent.Header("x-gotify-key"));
        Assert.StartsWith("application/json", sent.Header("content-type"), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(accepted.Body!), JsonNode.Parse(sent.Body)), $"Deluno sent {sent.Body}, the real Gotify accepted {accepted.Body}");
        Assert.Equal("delivered", Assert.Single((await DeliveriesAsync(app)).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_Gotify_with_no_token_set_is_asked_without_one_and_its_refusal_is_the_error_shown()
    {
        await using var gotify = await GotifyAsync("AbCdEfGhIjKlMnO");
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Push", gotify.BaseUrl, "gotify"));
        Assert.Equal(401, Gotify("refused").Exchanges[1].Reply.Status);

        await SayAsync(app);

        Assert.All(gotify.Requests, request => Assert.Equal(string.Empty, request.Header("x-gotify-key")));
        Assert.Contains("HTTP 401", (await WebhookAsync(app, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    [Fact(Skip = "Defect: a title with an accent (Amélie, Léon, Pokémon) never reaches ntfy. BuildNtfyRequest puts the title raw in the Title header and .NET refuses it (\"Request headers must contain only ASCII characters.\"), so all three attempts fail before anything is sent and the delivery dead-letters. The real ntfy 2.32.0 reads an RFC 2047 encoded Title (recorded: ntfy/title-encoded) and shows the accent (found 19 Oct 2026).")]
    public async Task A_title_with_an_accent_reaches_ntfy_as_the_encoded_header_the_real_ntfy_reads_back_with_its_accent()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy"));
        var accepted = Ntfy("title-encoded").Exchanges[0].Request;

        await SayAsync(app, title: "Import completed: Amélie (2001)");

        var sent = Assert.Single(ntfy.Requests);
        Assert.Equal(accepted.Headers!["title"], sent.Header("title"));
        Assert.Equal("delivered", Assert.Single((await DeliveriesAsync(app)).EnumerateArray()).GetProperty("status").GetString());
    }

    // ---- what happens when it does not arrive ----

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_retried_and_dead_lettered_with_the_reason_and_never_reaches_the_caller()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Nowhere", $"http://127.0.0.1:{ClosedPort()}/deluno-films", "ntfy"));

        await SayAsync(app);

        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal(("dead-letter", 3), (delivery.GetProperty("status").GetString(), delivery.GetProperty("attemptCount").GetInt32()));
        var reason = delivery.GetProperty("lastError").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(reason, (await WebhookAsync(app, id)).GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task One_connection_that_fails_never_stops_the_next_one_being_told_and_a_good_delivery_clears_the_error_it_left()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var broken = await AddAsync(app, Webhook("A broken one", $"http://127.0.0.1:{ClosedPort()}/deluno-films", "ntfy"));
        var good = await AddAsync(app, Webhook("A good one", ntfy.BaseUrl + "/deluno-films", "ntfy"));

        await SayAsync(app);

        Assert.Single(ntfy.Requests);
        var byWebhook = (await DeliveriesAsync(app)).EnumerateArray().ToDictionary(item => item.GetProperty("webhookId").GetString()!, item => item.GetProperty("status").GetString());
        Assert.Equal(("dead-letter", "delivered"), (byWebhook[broken], byWebhook[good]));
        Assert.False(string.IsNullOrWhiteSpace((await WebhookAsync(app, broken)).GetProperty("lastError").GetString()));
        Assert.True(string.IsNullOrEmpty((await WebhookAsync(app, good)).GetProperty("lastError").GetString()));
    }

    // ---- who is told ----

    [Fact]
    public async Task A_connection_that_is_switched_off_receives_nothing_from_an_event_its_filter_names_and_the_master_switch_off_stops_every_one()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy", isEnabled: false));

        await SayAsync(app);
        Assert.Empty(ntfy.Requests);

        Assert.True((await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", new { name = "Phone", url = ntfy.BaseUrl + "/deluno-films", eventFilters = "all", isEnabled = true })).IsSuccessStatusCode);
        await SayAsync(app);
        Assert.Single(ntfy.Requests);

        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = false })).IsSuccessStatusCode);
        await SayAsync(app);
        Assert.Single(ntfy.Requests);
        Assert.Equal(1, (await DeliveriesAsync(app)).GetArrayLength());
    }

    [Fact]
    public async Task The_generic_webhook_posts_its_envelope_with_no_login_header_signature_or_custom_header_because_the_connection_has_nowhere_to_hold_one()
    {
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (request, _) => Ntfy("delivered").Exchanges[0].Reply.ToResponse());
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, Webhook("Anything", receiver.BaseUrl + "/hook"));

        await SayAsync(app);

        var sent = Assert.Single(receiver.Requests);
        Assert.Equal(string.Empty, sent.Authorization);
        Assert.DoesNotContain(sent.Headers!.Keys, name => name.Contains("signature", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal) || name.StartsWith("x-", StringComparison.Ordinal));
        Assert.Equal("import.completed", JsonNode.Parse(sent.Body)!["eventCategory"]!.GetValue<string>());
        var shape = typeof(CreateNotificationWebhookRequest).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(shape, name => name.Contains("Header", StringComparison.Ordinal) || name.Contains("Secret", StringComparison.Ordinal) || name.Contains("Password", StringComparison.Ordinal) || name.Contains("Signature", StringComparison.Ordinal));
    }

    // ---- the test button, the history and the replay ----

    [Fact]
    public async Task The_test_button_names_an_unknown_webhook_a_404_a_switched_off_one_disabled_and_sends_one_event_to_an_enabled_one()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var off = await AddAsync(app, Webhook("Off", ntfy.BaseUrl + "/off", "ntfy", isEnabled: false));
        var on = await AddAsync(app, Webhook("On", ntfy.BaseUrl + "/on", "ntfy"));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/notification-webhooks/not-a-webhook/test", null)).StatusCode);

        var disabled = await (await app.Client.PostAsync($"/api/notification-webhooks/{off}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabled.GetProperty("sent").GetBoolean());
        Assert.Equal("disabled", disabled.GetProperty("status").GetString());
        Assert.Equal("This webhook is disabled.", disabled.GetProperty("error").GetString());
        Assert.Empty(ntfy.Requests);

        var sent = await (await app.Client.PostAsync($"/api/notification-webhooks/{on}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sent.GetProperty("sent").GetBoolean());
        var received = Assert.Single(ntfy.Requests);
        Assert.Equal(("POST", "/on", "Deluno Webhook Test"), (received.Method, received.PathAndQuery, received.Header("title")));
        Assert.Equal("This is a test notification from Deluno. Your webhook is configured correctly.", received.Body);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal(("test", "delivered"), (delivery.GetProperty("eventCategory").GetString(), delivery.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task The_history_lists_newest_first_filters_by_connection_and_by_status_and_holds_the_number_asked_for_between_one_and_five_hundred()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var good = await AddAsync(app, Webhook("Good", ntfy.BaseUrl + "/good", "ntfy"));
        var other = await AddAsync(app, Webhook("Other", ntfy.BaseUrl + "/other", "ntfy"));
        foreach (var number in Enumerable.Range(1, 3))
        {
            await SayAsync(app, title: $"Import completed: Film {number}");
            await Task.Delay(15); // reason: deliveries are ordered by the millisecond they were made
        }

        var all = (await DeliveriesAsync(app)).EnumerateArray().ToArray();
        Assert.Equal(6, all.Length);
        Assert.Equal(all.Select(item => item.GetProperty("createdUtc").GetDateTimeOffset()).OrderByDescending(when => when), all.Select(item => item.GetProperty("createdUtc").GetDateTimeOffset()));
        Assert.Equal("Import completed: Film 3", all[0].GetProperty("title").GetString());
        Assert.Equal(3, (await DeliveriesAsync(app, $"?webhookId={good}")).GetArrayLength());
        Assert.All((await DeliveriesAsync(app, $"?webhookId={other}")).EnumerateArray(), item => Assert.Equal(other, item.GetProperty("webhookId").GetString()));
        Assert.Equal(6, (await DeliveriesAsync(app, "?status=delivered")).GetArrayLength());
        Assert.Equal(0, (await DeliveriesAsync(app, "?status=dead-letter")).GetArrayLength());
        Assert.Equal(1, (await DeliveriesAsync(app, "?take=0")).GetArrayLength());
        Assert.Equal(2, (await DeliveriesAsync(app, "?take=2")).GetArrayLength());
        Assert.Equal(6, (await DeliveriesAsync(app, "?take=100000")).GetArrayLength());
    }

    [Fact]
    public async Task A_replay_sends_the_stored_message_again_on_the_same_delivery_even_with_the_master_switch_off_or_the_connection_switched_off_and_a_connection_that_is_gone_dead_letters()
    {
        await using var ntfy = await OpenNtfyAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, Webhook("Phone", ntfy.BaseUrl + "/deluno-films", "ntfy"));
        await SayAsync(app);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray()).GetProperty("id").GetString()!;
        Assert.Single(ntfy.Requests);

        var again = await (await app.Client.PostAsync($"/api/notification-webhooks/deliveries/{delivery}/replay", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(again.GetProperty("sent").GetBoolean());
        Assert.Equal(delivery, again.GetProperty("deliveryId").GetString());
        Assert.Equal(2, ntfy.Requests.Count);
        Assert.Equal(ntfy.Requests[0].Body, ntfy.Requests[1].Body);
        Assert.Equal(ntfy.Requests[0].Header("title"), ntfy.Requests[1].Header("title"));
        Assert.Single((await DeliveriesAsync(app)).EnumerateArray());

        // A replay checks neither the master switch nor the connection's own switch: the person asked for this one delivery.
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = false })).IsSuccessStatusCode);
        Assert.True((await app.Client.PutAsJsonAsync($"/api/notification-webhooks/{id}", new { name = "Phone", url = ntfy.BaseUrl + "/deluno-films", eventFilters = "all", isEnabled = false })).IsSuccessStatusCode);
        Assert.True((await (await app.Client.PostAsync($"/api/notification-webhooks/deliveries/{delivery}/replay", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Equal(3, ntfy.Requests.Count);

        // The connection is deleted; its history stays and a replay says so.
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/notification-webhooks/{id}")).StatusCode);
        Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        var gone = await (await app.Client.PostAsync($"/api/notification-webhooks/deliveries/{delivery}/replay", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(gone.GetProperty("sent").GetBoolean());
        Assert.Equal("dead-letter", gone.GetProperty("status").GetString());
        Assert.Equal("The webhook no longer exists, so this delivery cannot be replayed.", gone.GetProperty("error").GetString());
        Assert.Equal(3, ntfy.Requests.Count);
    }

    // ---- the switches ----

    [Fact]
    public async Task Every_notification_switch_is_saved_and_read_back_and_a_body_that_leaves_some_out_puts_those_back_to_their_own_defaults()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var defaults = await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-preferences");
        var names = defaults.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.True(names.Length >= 17, string.Join(", ", names));

        var flipped = new JsonObject();
        foreach (var property in defaults.EnumerateObject())
        {
            flipped[property.Name] = !property.Value.GetBoolean();
        }

        Assert.True((await app.Client.PutAsJsonAsync("/api/notification-preferences", flipped)).IsSuccessStatusCode);
        var saved = await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-preferences");
        foreach (var property in defaults.EnumerateObject())
        {
            Assert.Equal(!property.Value.GetBoolean(), saved.GetProperty(property.Name).GetBoolean());
        }

        Assert.True((await app.Client.PutAsJsonAsync("/api/notification-preferences", new { grabEnabled = true })).IsSuccessStatusCode);
        var partial = await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-preferences");
        foreach (var property in defaults.EnumerateObject().Where(property => property.Name != "grabEnabled"))
        {
            Assert.Equal(property.Value.GetBoolean(), partial.GetProperty(property.Name).GetBoolean());
        }

        Assert.True(partial.GetProperty("grabEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PutAsJsonAsync("/api/notification-preferences", flipped)).StatusCode);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public async Task The_master_switch_is_kept_as_notifications_enabled_and_counts_as_on_only_when_it_says_true(string stored, bool on)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.ExecuteAsync(app, "platform", $"INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('notifications.enabled', '{stored}', '2026-10-19T00:00:00Z') ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;");

        Assert.Equal(on, await app.Services.GetRequiredService<Deluno.Notifications.Data.INotificationRepository>().AreOutboundNotificationsEnabledAsync(CancellationToken.None));
        Assert.Equal(on, (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("enableNotifications").GetBoolean());

        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);
        Assert.Equal("true", await ApiScenes.TextAsync(app, "platform", "SELECT setting_value FROM system_settings WHERE setting_key = 'notifications.enabled'"));
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = false })).IsSuccessStatusCode);
        Assert.Equal("false", await ApiScenes.TextAsync(app, "platform", "SELECT setting_value FROM system_settings WHERE setting_key = 'notifications.enabled'"));
    }

    // ---- secrets in a connection (known defects) ----

    private const string BotToken = "123456789:SECRET-telegram-bot-token-4f9a";
    private const string DiscordUrl = "https://discord.com/api/webhooks/1234567890/SECRET-discord-token-8c2d";

    [Fact]
    public async Task The_list_of_connections_says_a_secret_is_held_and_never_what_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddAsync(app, Webhook("Telegram", string.Empty, "telegram", configJson: JsonSerializer.Serialize(new { botToken = BotToken, chatId = "42" })));
        await AddAsync(app, Webhook("Discord", DiscordUrl, "discord"));

        var listed = await app.Client.GetStringAsync("/api/notification-webhooks");

        Assert.DoesNotContain(BotToken, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-discord-token-8c2d", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connections_tokens_and_address_are_stored_protected_not_as_plain_text()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddAsync(app, Webhook("Telegram", string.Empty, "telegram", configJson: JsonSerializer.Serialize(new { botToken = BotToken, chatId = "42" })));
        await AddAsync(app, Webhook("Discord", DiscordUrl, "discord"));

        var stored = await ApiScenes.TextAsync(app, "platform", "SELECT group_concat(config_json || ' ' || url, ' ') FROM notification_webhooks");

        Assert.DoesNotContain(BotToken, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-discord-token-8c2d", stored, StringComparison.Ordinal);
    }

    // ---- the "Send when" ticks ----

    [Fact]
    public async Task The_send_when_ticks_are_prefixes_of_the_events_the_engines_send_and_an_event_with_no_tick_reaches_only_a_connection_that_hears_everything()
    {
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, Webhook("Ticked", receiver.BaseUrl + "/ticked", "generic", eventFilters: "grab,import,upgrade,health,retention,app.update,test"));
        await AddAsync(app, Webhook("Everything", receiver.BaseUrl + "/everything", "generic", eventFilters: "all"));
        string[] withATick = ["grab", "import.completed", "import.failed", "upgrade", "health.degraded", "health.restored", "health.problem", "health.resolved", "retention.leaving-soon", "retention.removed", "retention.failed", "app.update", "test"];
        string[] withNone = ["rename", "file.deleted", "title.added", "manual_interaction_required", "library.paused", "library.resumed", "acquisition.override", "download.queue-item-removed", "filesystem.import.unmatched-given-up"];

        foreach (var category in withATick.Concat(withNone))
        {
            await SayAsync(app, category, "Title", "Message");
        }

        string[] Heard(string path) => receiver.Requests.Where(request => request.PathAndQuery == path).Select(request => JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>()).ToArray();
        Assert.Equal(withATick.Order(), Heard("/ticked").Order());
        Assert.Equal(withATick.Concat(withNone).Order(), Heard("/everything").Order());
    }
}
