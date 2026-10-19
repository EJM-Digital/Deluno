using System.Net;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Dedicated notification providers beyond the generic webhook and sniffed
/// Discord embed (#855): each provider routes to its own request shape,
/// verified against each service's own documented API.
/// </summary>
public sealed class NotificationProviderPayloadTests
{
    private static async Task EnableOutboundNotificationsAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform,
            CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('notifications.enabled', 'true', '2026-09-27T02:00:00Z') " +
            "ON CONFLICT(setting_key) DO UPDATE SET setting_value = 'true';";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<(OutboundNotificationService Service, CapturingHandler Handler)> CreateServiceAsync(
        TestStorage storage,
        string provider,
        string url,
        string? configJson)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Test", url, "all", true, provider, configJson),
            CancellationToken.None);

        var handler = new CapturingHandler();
        var service = new OutboundNotificationService(repository, new SingleClientFactory(handler), NullLogger<OutboundNotificationService>.Instance);
        return (service, handler);
    }

    [Fact]
    public async Task Slack_provider_sends_a_text_payload_to_the_webhook_url()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Slack, "https://hooks.slack.com/services/x", null);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://hooks.slack.com/services/x", handler.LastRequestUri);
        Assert.Contains("\"text\"", handler.LastRequestBody);
        Assert.Contains("The Matrix (1999)", handler.LastRequestBody);
    }

    [Fact]
    public async Task Discord_provider_sends_one_embed_with_the_title_message_footer_and_the_colour_of_its_event()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Discord, "https://discord.com/api/webhooks/1/abc", null);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://discord.com/api/webhooks/1/abc", handler.LastRequestUri);
        using var body = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var embed = Assert.Single(body.RootElement.GetProperty("embeds").EnumerateArray());
        Assert.Equal("Imported", embed.GetProperty("title").GetString());
        Assert.Equal("The Matrix (1999)", embed.GetProperty("description").GetString());
        Assert.Equal(0x57F287, embed.GetProperty("color").GetInt32());
        Assert.Equal("Deluno • import.completed", embed.GetProperty("footer").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_generic_webhook_pointed_at_a_discord_address_is_sent_as_a_discord_embed_and_any_other_address_keeps_the_generic_shape()
    {
        using var discordStorage = TestStorage.Create();
        var (discord, discordHandler) = await CreateServiceAsync(discordStorage, NotificationProviders.Generic, "https://discord.com/api/webhooks/2/def", null);
        await discord.DispatchAsync("import.failed", "Import failed", "Dune (2021)", null, CancellationToken.None);
        using (var body = System.Text.Json.JsonDocument.Parse(discordHandler.LastRequestBody!))
        {
            Assert.Equal(0xED4245, Assert.Single(body.RootElement.GetProperty("embeds").EnumerateArray()).GetProperty("color").GetInt32());
        }

        using var otherStorage = TestStorage.Create();
        var (other, otherHandler) = await CreateServiceAsync(otherStorage, NotificationProviders.Generic, "https://hooks.example.test/plain", null);
        await other.DispatchAsync("import.failed", "Import failed", "Dune (2021)", null, CancellationToken.None);
        using var plain = System.Text.Json.JsonDocument.Parse(otherHandler.LastRequestBody!);
        Assert.False(plain.RootElement.TryGetProperty("embeds", out _));
    }

    [Fact]
    public async Task Telegram_provider_posts_to_the_bot_api_with_chat_id_and_text()
    {
        using var storage = TestStorage.Create();
        var configJson = """{"botToken":"123:ABC","chatId":"555"}""";
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Telegram, string.Empty, configJson);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", handler.LastRequestUri);
        Assert.Contains("\"chat_id\":\"555\"", handler.LastRequestBody);
        Assert.Contains("The Matrix (1999)", handler.LastRequestBody);
    }

    [Fact]
    public async Task Pushover_provider_posts_form_encoded_token_user_and_message()
    {
        using var storage = TestStorage.Create();
        var configJson = """{"appToken":"apptok","userKey":"userkey"}""";
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Pushover, string.Empty, configJson);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://api.pushover.net/1/messages.json", handler.LastRequestUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.LastContentType);
        Assert.Contains("token=apptok", handler.LastRequestBody);
        Assert.Contains("user=userkey", handler.LastRequestBody);
    }

    [Fact]
    public async Task Ntfy_provider_posts_plain_text_body_with_title_header()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Ntfy, "https://ntfy.sh/deluno-topic", null);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://ntfy.sh/deluno-topic", handler.LastRequestUri);
        Assert.Equal("Imported", handler.LastTitleHeader);
        Assert.Equal("The Matrix (1999)", handler.LastRequestBody);
    }

    [Fact]
    public async Task Gotify_provider_posts_json_with_the_app_token_header()
    {
        using var storage = TestStorage.Create();
        var configJson = """{"appToken":"gotifytoken"}""";
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Gotify, "https://gotify.example.test", configJson);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://gotify.example.test/message", handler.LastRequestUri);
        Assert.Equal("gotifytoken", handler.LastGotifyKeyHeader);
        Assert.Contains("The Matrix (1999)", handler.LastRequestBody);
    }

    [Fact]
    public async Task Apprise_provider_posts_the_default_title_message_json_shape()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Apprise, "https://apprise.example.test/notify", null);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Equal("https://apprise.example.test/notify", handler.LastRequestUri);
        Assert.Contains("\"title\"", handler.LastRequestBody);
        Assert.Contains("\"message\"", handler.LastRequestBody);
        Assert.Contains("The Matrix (1999)", handler.LastRequestBody);
    }

    [Fact]
    public async Task Generic_payload_carries_the_machines_hostname()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Generic, "https://example.test/hook", null);

        await service.DispatchAsync("import.completed", "Imported", "Akira", null, CancellationToken.None);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(Environment.MachineName, doc.RootElement.GetProperty("instanceName").GetString());
    }

    [Fact]
    public async Task Generic_payload_carries_the_titles_tag_names_and_a_title_less_event_has_none()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Generic, "https://example.test/hook", null);

        await service.DispatchAsync("import.completed", "Imported", "Akira", null, CancellationToken.None, titleTags: ["anime", "4k"]);
        using (var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal(["anime", "4k"], doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        }

        await service.DispatchAsync("import.completed", "Imported", "Plain", null, CancellationToken.None, titleTags: []);
        using (var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("tags").GetArrayLength());
        }

        await service.DispatchAsync("health.warning", "Indexer down", "x", null, CancellationToken.None, titleTags: null);
        using (var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("tags").ValueKind);
        }
    }

    [Fact]
    public async Task Chat_providers_append_the_titles_tags_to_the_message()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Ntfy, "https://ntfy.sh/t", null);

        await service.DispatchAsync("import.completed", "Imported", "Akira", null, CancellationToken.None, titleTags: ["anime", "4k"]);

        Assert.Equal("Akira\nTags: anime, 4k", handler.LastRequestBody);
    }

    [Fact]
    public async Task Generic_provider_keeps_sending_the_original_event_envelope()
    {
        using var storage = TestStorage.Create();
        var (service, handler) = await CreateServiceAsync(storage, NotificationProviders.Generic, "https://example.test/hook", null);

        await service.DispatchAsync("import.completed", "Imported", "The Matrix (1999)", null, CancellationToken.None);

        Assert.Contains("\"eventCategory\"", handler.LastRequestBody);
        Assert.Contains("import.completed", handler.LastRequestBody);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastTitleHeader { get; private set; }
        public string? LastGotifyKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastTitleHeader = request.Headers.TryGetValues("Title", out var titles) ? titles.FirstOrDefault() : null;
            LastGotifyKeyHeader = request.Headers.TryGetValues("X-Gotify-Key", out var keys) ? keys.FirstOrDefault() : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
