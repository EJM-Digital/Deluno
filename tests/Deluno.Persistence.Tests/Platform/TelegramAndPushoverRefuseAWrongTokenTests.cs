using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1301: what Deluno records when Telegram or Pushover refuse the token a message was sent with, against each service's own recorded
/// refusal (tests/fixtures/recorded/telegram and pushover, made by scripts/recorded-replies/docker/notify-refusals.mjs with a made-up token).
/// </summary>
public sealed class TelegramAndPushoverRefuseAWrongTokenTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Replying(RecordedReply reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return reply.ToResponse();
        }
    }

    private static async Task<(NotificationWebhookDeliveryResult Result, Replying Handler)> SendAsync(string provider, string configJson, RecordedReply reply)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T02:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await using (var connection = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('notifications.enabled', 'true', '2026-09-27T02:00:00Z') ON CONFLICT(setting_key) DO UPDATE SET setting_value = 'true';";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var repository = new SqliteNotificationRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var webhook = await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Phone", string.Empty, "all", true, provider, configJson), CancellationToken.None);
        var handler = new Replying(reply);
        var service = new OutboundNotificationService(repository, new Factory(handler), NullLogger<OutboundNotificationService>.Instance);
        var result = await service.DispatchToWebhookAsync(webhook.Id, "import.completed", "Import completed: Sintel (2010)", "Sintel (2010) was imported.", null, CancellationToken.None);
        return (result!, handler);
    }

    [Fact]
    public async Task Telegram_refusing_the_bot_token_with_401_is_not_sent_and_the_401_is_the_recorded_one()
    {
        var recorded = RecordedReplies.Telegram("wrong-token").Exchanges[0].Reply;
        Assert.Equal(401, recorded.Status);

        var (result, handler) = await SendAsync(NotificationProviders.Telegram, """{"botToken":"123456:wrongtokenrecordedbydelunotests","chatId":"42"}""", recorded);

        Assert.False(result.Sent);
        Assert.Contains("401", result.Error ?? string.Empty);
        Assert.Contains("/bot123456:wrongtokenrecordedbydelunotests/sendMessage", handler.Request!.RequestUri!.ToString());
        Assert.Contains("\"chat_id\":\"42\"", handler.Body);
        Assert.Contains("Import completed: Sintel (2010)", handler.Body);
        Assert.Equal("dead-letter", result.Status);
        Assert.Equal(401, result.Failure!.HttpStatus);
    }

    [Fact]
    public async Task Pushover_refusing_the_application_token_with_400_is_not_sent_and_says_400()
    {
        var recorded = RecordedReplies.Pushover("wrong-token").Exchanges[0].Reply;
        Assert.Equal(400, recorded.Status);

        var (result, handler) = await SendAsync(NotificationProviders.Pushover, """{"appToken":"wrongtokenrecordedbydelunotests","userKey":"wrongtokenrecordedbydelunotests"}""", recorded);

        Assert.False(result.Sent);
        Assert.Contains("400", result.Error ?? string.Empty);
        Assert.Contains("token=wrongtokenrecordedbydelunotests", handler.Body);
        Assert.Equal(400, result.Failure!.HttpStatus);
    }
}
