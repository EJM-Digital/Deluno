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

    /// <summary>
    /// #524: "All events" is stored as <c>all</c>, which is no category's
    /// prefix, so those webhooks received only tests. Stored rows are covered
    /// as they are; a filtered webhook still receives only its own.
    /// </summary>
    [Theory]
    [InlineData("all", "import.failed", true)]
    [InlineData("All", "retention.removed", true)]
    [InlineData("*", "health.alert", true)]
    [InlineData("import", "import.failed", true)]
    [InlineData("import", "grab.sent", false)]
    [InlineData("import,retention", "retention.leaving-soon", true)]
    public async Task A_webhook_receives_the_categories_its_filter_names(string filters, string category, bool delivered)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-17T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);
        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Everything", "https://hooks.example.test/all", filters, true), CancellationToken.None);
        var handler = new SequencedHandler([]);
        var service = new OutboundNotificationService(repository, new SingleClientFactory(handler), NullLogger<OutboundNotificationService>.Instance);

        await service.DispatchAsync(category, "Title", "Message", null, CancellationToken.None);

        Assert.Equal(delivered ? 1 : 0, handler.Attempts);
    }

    /// <summary>
    /// #990: a notification connection carrying tags fires only for a title
    /// sharing one of them; an untagged connection fires for any title (or
    /// an event that names no title, since <c>titleTags</c> is then null) —
    /// the same rule already applied to download clients and indexers.
    /// </summary>
    [Fact]
    public async Task A_tagged_webhook_fires_only_for_a_matching_title_and_an_untagged_webhook_fires_for_any_title()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);
        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Anime channel", "https://hooks.example.test/anime", "all", true, Tags: "anime"),
            CancellationToken.None);
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Everything channel", "https://hooks.example.test/all", "all", true),
            CancellationToken.None);
        // A title with no anime tag: only the untagged channel fires.
        var untaggedTitleHandler = new SequencedHandler([]);
        var untaggedTitleService = new OutboundNotificationService(repository, new SingleClientFactory(untaggedTitleHandler), NullLogger<OutboundNotificationService>.Instance);
        await untaggedTitleService.DispatchAsync("import.completed", "Some Movie", "Message", null, CancellationToken.None, titleTags: ["kids"]);
        Assert.Equal(1, untaggedTitleHandler.Attempts);

        // A title tagged anime: both channels fire.
        var animeTitleHandler = new SequencedHandler([]);
        var animeTitleService = new OutboundNotificationService(repository, new SingleClientFactory(animeTitleHandler), NullLogger<OutboundNotificationService>.Instance);
        await animeTitleService.DispatchAsync("import.completed", "Some Anime", "Message", null, CancellationToken.None, titleTags: ["anime"]);
        Assert.Equal(2, animeTitleHandler.Attempts);
    }

    /// <summary>
    /// #1036: tags only filter notifications about a title. An event that
    /// names no title (titleTags null, e.g. a health warning) reaches a tagged
    /// connection too, while a title event with non-matching tags still skips it.
    /// </summary>
    [Fact]
    public async Task A_tagged_webhook_receives_events_that_are_not_about_a_title_but_still_skips_a_non_matching_title()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);
        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Anime channel", "https://hooks.example.test/anime", "all", true, Tags: "anime"),
            CancellationToken.None);

        var healthHandler = new SequencedHandler([]);
        await new OutboundNotificationService(repository, new SingleClientFactory(healthHandler), NullLogger<OutboundNotificationService>.Instance)
            .DispatchAsync("health.warning", "Indexer down", "Message", null, CancellationToken.None, titleTags: null);
        Assert.Equal(1, healthHandler.Attempts);

        var otherTitleHandler = new SequencedHandler([]);
        await new OutboundNotificationService(repository, new SingleClientFactory(otherTitleHandler), NullLogger<OutboundNotificationService>.Instance)
            .DispatchAsync("import.completed", "Some Movie", "Message", null, CancellationToken.None, titleTags: ["kids"]);
        Assert.Equal(0, otherTitleHandler.Attempts);
    }

    /// <summary>
    /// #1016: Radarr and Sonarr's per-connection "Include Health Warnings". An
    /// advisory-tier health event reaches only a connection that turned the
    /// switch on; a problem that stops Deluno working reaches every connection
    /// that listens for health; a new connection starts with the switch off.
    /// </summary>
    [Fact]
    public async Task A_health_warning_reaches_only_a_connection_that_included_health_warnings()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);
        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var errorsOnly = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Errors only", "https://hooks.example.test/errors", "health", true),
            CancellationToken.None);
        Assert.False(errorsOnly.IncludeHealthWarnings);
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("With warnings", "https://hooks.example.test/warnings", "health", true, IncludeHealthWarnings: true),
            CancellationToken.None);

        var warningHandler = new SequencedHandler([]);
        await new OutboundNotificationService(repository, new SingleClientFactory(warningHandler), NullLogger<OutboundNotificationService>.Instance)
            .DispatchAsync("health.problem" + HealthCheckCategories.OutboundWarningSuffix, "Metadata", "Message", null, CancellationToken.None);
        Assert.Equal(1, warningHandler.Attempts);

        var errorHandler = new SequencedHandler([]);
        await new OutboundNotificationService(repository, new SingleClientFactory(errorHandler), NullLogger<OutboundNotificationService>.Instance)
            .DispatchAsync("health.problem", "Download client", "Message", null, CancellationToken.None);
        Assert.Equal(2, errorHandler.Attempts);

        var turnedOn = await repository.UpdateNotificationWebhookAsync(
            errorsOnly.Id,
            new UpdateNotificationWebhookRequest("Errors only", "https://hooks.example.test/errors", "health", true, IncludeHealthWarnings: true),
            CancellationToken.None);
        Assert.True(turnedOn!.IncludeHealthWarnings);
        var untouched = await repository.UpdateNotificationWebhookAsync(
            errorsOnly.Id,
            new UpdateNotificationWebhookRequest("Errors only", "https://hooks.example.test/errors", "health", true),
            CancellationToken.None);
        Assert.True(untouched!.IncludeHealthWarnings);
    }

    /// <summary>
    /// #1017: a connection picks the "Deluno update" trigger by name; one that
    /// listens for something else does not receive it.
    /// </summary>
    [Theory]
    [InlineData("app.update", true)]
    [InlineData("grab,app.update", true)]
    [InlineData("import", false)]
    public async Task A_connection_receives_the_application_update_event_only_when_it_lists_it(string filters, bool delivered)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);
        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Updates", "https://hooks.example.test/u", filters, true), CancellationToken.None);
        var handler = new SequencedHandler([]);

        await new OutboundNotificationService(repository, new SingleClientFactory(handler), NullLogger<OutboundNotificationService>.Instance)
            .DispatchAsync("app.update", "Deluno updated", "Message", null, CancellationToken.None);

        Assert.Equal(delivered ? 1 : 0, handler.Attempts);
    }

    /// <summary>
    /// #1036: an episode event is about its show, so it carries the show's
    /// tags. A show tagged "kids" is skipped by a connection tagged "adults"
    /// and reaches one tagged "kids"; an unknown episode is title-less.
    /// </summary>
    [Fact]
    public async Task An_episode_event_uses_its_shows_tags_so_a_non_matching_tagged_webhook_is_skipped()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T02:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await new Deluno.Series.Data.SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<Deluno.Series.Data.SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await EnableOutboundNotificationsAsync(storage);

        var series = new Deluno.Series.Data.SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        await series.AddAsync(new Deluno.Series.Contracts.CreateSeriesRequest("Kids Show", 2022, null), CancellationToken.None);
        var seriesId = (await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.Single().Id;
        await using (var connection = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Series, CancellationToken.None))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO episode_entries (id, series_id, season_number, episode_number, title, monitored, has_file, quality_cutoff_met, created_utc, updated_utc)
                VALUES ('ep-1', '{seriesId}', 1, 1, 'Pilot', 1, 0, 0, '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z');
                """;
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var tagStore = new Deluno.Media.SqliteMediaTagStore(storage.Factory, timeProvider);
        await tagStore.ReplaceAsync(MediaKind.Series, seriesId, [new MediaTagAssignment("tag-kids", "kids")], CancellationToken.None);

        var episodeTags = await tagStore.ListTitleTagNamesAsync("episode", "ep-1", CancellationToken.None);
        Assert.Equal(["kids"], episodeTags);
        Assert.Null(await tagStore.ListTitleTagNamesAsync("episode", "no-such-episode", CancellationToken.None));

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Adults channel", "https://hooks.example.test/adults", "all", true, Tags: "adults"),
            CancellationToken.None);

        var handler = new SequencedHandler([]);
        var service = new OutboundNotificationService(repository, new SingleClientFactory(handler), NullLogger<OutboundNotificationService>.Instance);
        await service.DispatchAsync("grab", "Grabbed: Kids Show S01E01", "Message", null, CancellationToken.None, titleTags: episodeTags);
        Assert.Equal(0, handler.Attempts);

        await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Kids channel", "https://hooks.example.test/kids", "all", true, Tags: "kids"),
            CancellationToken.None);
        await service.DispatchAsync("grab", "Grabbed: Kids Show S01E01", "Message", null, CancellationToken.None, titleTags: episodeTags);
        Assert.Equal(1, handler.Attempts);
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

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
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

        var repository = new SqliteNotificationRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
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
            timeProvider,
            TestSecretProtection.Create(storage));

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
