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

public sealed class NotificationWebhookDeliveryTests
{
    /// <summary>
    /// DispatchAsync is gated on the user's master delivery switch (#253);
    /// these tests exercise delivery itself, so the switch goes on first.
    /// </summary>
    private static async Task EnableOutboundNotificationsAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform,
            CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('notifications.enabled', 'true', '2026-05-14T02:00:00Z') " +
            "ON CONFLICT(setting_key) DO UPDATE SET setting_value = 'true';";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchAsync_retries_failed_delivery_then_records_success()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var repository = new SqliteNotificationRepository(
            storage.Factory,
            timeProvider);

        var webhook = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest(
                Name: "Operations",
                Url: "https://hooks.example.test/ops",
                EventFilters: "movies",
                IsEnabled: true),
            CancellationToken.None);

        var handler = new SequencedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);
        var service = new OutboundNotificationService(
            repository,
            new SingleClientFactory(handler),
            NullLogger<OutboundNotificationService>.Instance);

        await service.DispatchAsync(
            eventCategory: "movies.search.completed",
            title: "Search completed",
            message: "A movie search cycle completed.",
            detailsJson: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, handler.Attempts);

        var saved = (await repository.ListNotificationWebhooksAsync(CancellationToken.None))
            .Single(item => item.Id == webhook.Id);
        Assert.NotNull(saved.LastFiredUtc);
        Assert.Null(saved.LastError);
    }

    [Fact]
    public async Task DispatchAsync_skips_delivery_when_event_filter_does_not_match()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var repository = new SqliteNotificationRepository(
            storage.Factory,
            timeProvider);

        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest(
                Name: "Operations",
                Url: "https://hooks.example.test/ops",
                EventFilters: "series",
                IsEnabled: true),
            CancellationToken.None);

        var handler = new SequencedHandler([new HttpResponseMessage(HttpStatusCode.OK)]);
        var service = new OutboundNotificationService(
            repository,
            new SingleClientFactory(handler),
            NullLogger<OutboundNotificationService>.Instance);

        await service.DispatchAsync(
            eventCategory: "movies.search.completed",
            title: "Search completed",
            message: "A movie search cycle completed.",
            detailsJson: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task DispatchAsync_skips_every_delivery_while_the_master_switch_is_off()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        // The master switch is deliberately left unset: only an explicit
        // "true" counts as on, matching the settings page display.

        var repository = new SqliteNotificationRepository(
            storage.Factory,
            timeProvider);

        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest(
                Name: "Operations",
                Url: "https://hooks.example.test/ops",
                EventFilters: "",
                IsEnabled: true),
            CancellationToken.None);

        var handler = new SequencedHandler([new HttpResponseMessage(HttpStatusCode.OK)]);
        var service = new OutboundNotificationService(
            repository,
            new SingleClientFactory(handler),
            NullLogger<OutboundNotificationService>.Instance);

        await service.DispatchAsync(
            eventCategory: "movies.search.completed",
            title: "Search completed",
            message: "A movie search cycle completed.",
            detailsJson: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, handler.Attempts);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SequencedHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
