using System.Net;
using Deluno.Contracts;
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

        var deliveries = await repository.ListNotificationWebhookDeliveriesAsync(
            NotificationWebhookDeliveryStatuses.Delivered,
            webhook.Id,
            10,
            CancellationToken.None);
        var delivery = Assert.Single(deliveries);
        Assert.Equal("movies.search.completed", delivery.EventCategory);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal(200, delivery.LastStatusCode);
        Assert.Null(delivery.LastError);
        Assert.Null(delivery.Failure);
    }

    [Fact]
    public async Task DispatchAsync_dead_letter_persists_a_typed_failure_for_replay_diagnosis()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider);
        var webhook = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Operations", "https://hooks.example.test/ops", "", true),
            CancellationToken.None);
        var handler = new SequencedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.BadGateway)
        ]);
        var service = new OutboundNotificationService(
            repository,
            new SingleClientFactory(handler),
            NullLogger<OutboundNotificationService>.Instance);

        var result = await service.DispatchToWebhookAsync(
            webhook.Id,
            "automation.failed",
            "Automation failed",
            "The typed failure should survive restart.",
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Sent);
        Assert.NotNull(result.Failure);
        Assert.Equal(IntegrationFailureKind.Unavailable, result.Failure!.Kind);
        Assert.Equal("notification-webhook", result.Failure.ServiceType);
        Assert.Equal(3, result.Failure.Attempts);

        var saved = await repository.GetNotificationWebhookDeliveryAsync(result.DeliveryId!, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(NotificationWebhookDeliveryStatuses.DeadLetter, saved.Item.Status);
        Assert.Null(saved.Item.NextAttemptUtc);
        Assert.NotNull(saved.Item.Failure);
        Assert.Equal(IntegrationFailureKind.Unavailable, saved.Item.Failure!.Kind);
        Assert.Equal(IntegrationRetryState.ManualAction, saved.Item.Failure.RetryState);
        Assert.DoesNotContain("will retry", saved.Item.Failure.NextAction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replay", saved.Item.Failure.NextAction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Failure.Message, saved.Item.Failure.Message);
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
    public async Task Replay_resends_the_persisted_payload_and_marks_the_same_delivery_delivered()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider);
        var webhook = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Operations", "https://hooks.example.test/ops", "", true),
            CancellationToken.None);
        var delivery = await repository.CreateNotificationWebhookDeliveryAsync(
            webhook.Id,
            "automation.failed",
            "Automation failed",
            "The saved event should be replayed.",
            "{\"libraryId\":\"library-1\"}",
            CancellationToken.None);

        var handler = new SequencedHandler([new HttpResponseMessage(HttpStatusCode.OK)]);
        var service = new OutboundNotificationService(
            repository,
            new SingleClientFactory(handler),
            NullLogger<OutboundNotificationService>.Instance);

        var result = await service.ReplayAsync(delivery.Item.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Sent);
        Assert.Equal(delivery.Item.Id, result.DeliveryId);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, handler.Attempts);

        var saved = await repository.GetNotificationWebhookDeliveryAsync(delivery.Item.Id, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(NotificationWebhookDeliveryStatuses.Delivered, saved.Item.Status);
        Assert.Equal("{\"libraryId\":\"library-1\"}", saved.DetailsJson);
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
