using System.Net;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class NotificationWebhookDeliveryTests
{
    [Fact]
    public async Task DispatchAsync_retries_failed_delivery_then_records_success()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

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

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

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
