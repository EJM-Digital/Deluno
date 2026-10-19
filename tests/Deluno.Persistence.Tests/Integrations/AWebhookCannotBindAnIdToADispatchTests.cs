using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1062: a download client's webhook is a caller's claim. It can report that
/// a download of Deluno's finished, but it can never change which client item
/// a dispatch is bound to: an id already there stays, and it never gives an id
/// to a dispatch (every dispatch has the one Deluno knew when it sent it). It
/// reaches a dispatch by the dispatch's own id, or by a hash or id Deluno
/// already recorded, and never by a release name.
/// </summary>
public sealed class AWebhookCannotBindAnIdToADispatchTests
{
    private const string Release = "Sent.Movie.2026.1080p.WEB";

    [Fact]
    public async Task A_dispatch_that_has_an_id_keeps_it_whatever_hash_the_webhook_names()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            world.ClientId,
            new DownloadClientWebhookRequest("completed", dispatchId, "someone-elses-hash", null, null, 10, null),
            CancellationToken.None);

        Assert.True(result.Accepted);
        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("real-hash", dispatch!.TorrentHashOrItemId);
        Assert.NotNull(dispatch.ClientCompletedUtc);
    }

    [Fact]
    public async Task A_dispatch_without_an_id_never_takes_a_hash_from_a_webhook_whatever_the_client_shows()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: null);

        await world.Webhook.HandleAsync(
            world.ClientId,
            new DownloadClientWebhookRequest("completed", dispatchId, "SENT-HASH", null, null, 10, null),
            CancellationToken.None);

        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(dispatch!.TorrentHashOrItemId));
    }

    [Fact]
    public async Task A_hash_Deluno_already_recorded_reaches_its_dispatch_ignoring_case()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest("completed", null, "REAL-HASH", null, null, 10, null), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(dispatchId, result.DispatchId);
    }

    [Fact]
    public async Task A_hash_Deluno_never_recorded_reaches_nothing()
    {
        using var world = await World.CreateAsync();
        await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest("completed", null, "someone-elses-hash", null, null, 10, null), CancellationToken.None);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task A_name_alone_reaches_nothing_even_for_a_dispatch_sent_a_moment_ago()
    {
        using var world = await World.CreateAsync();
        await world.SendAsync(existingId: null);

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest("completed", null, null, Release, null, 10, null), CancellationToken.None);

        Assert.False(result.Accepted);
    }

    // ---- #1301, shared-download-client-l.x8: what a completion or failure a client script reports does to the dispatch ----

    [Theory]
    [InlineData("completed")]
    [InlineData("download.completed")]
    [InlineData("torrent_completed")]
    [InlineData("nzb_completed")]
    [InlineData("finished")]
    public async Task A_completion_in_any_of_the_clients_words_marks_the_download_seen_and_leaves_a_timeline_entry(string eventName)
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest(eventName, dispatchId, null, null, "/downloads/done", 123456, null), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("Download completion recorded.", result.Message);
        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotNull(dispatch!.ClientCompletedUtc);
        Assert.Equal(123456, dispatch.DownloadedBytes);
        var timeline = await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        var entry = Assert.Single(timeline, e => e.EventType == "webhook-completed");
        Assert.Contains("/downloads/done", entry.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_completion_after_polling_or_an_earlier_webhook_saw_it_is_skipped_and_adds_nothing()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");
        var completed = new DownloadClientWebhookRequest("completed", dispatchId, null, null, null, 10, null);
        await world.Webhook.HandleAsync(world.ClientId, completed, CancellationToken.None);

        var again = await world.Webhook.HandleAsync(world.ClientId, completed, CancellationToken.None);

        Assert.True(again.Accepted);
        Assert.Contains("skipped duplicate", again.Message, StringComparison.Ordinal);
        var timeline = await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        Assert.Single(timeline, e => e.EventType == "webhook-completed");
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("download.failed")]
    [InlineData("torrent_failed")]
    [InlineData("nzb_failed")]
    [InlineData("error")]
    public async Task A_failure_the_client_reports_ends_the_dispatch_as_failed_with_the_clients_own_reason(string eventName)
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest(eventName, dispatchId, null, null, null, null, "Aborted, ran out of articles"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("Download failure recorded.", result.Message);
        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("failed", dispatch!.ImportStatus);
        Assert.Equal("Aborted, ran out of articles", dispatch.ImportFailureMessage);
        var timeline = await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        Assert.Single(timeline, e => e.EventType == "webhook-failed");
    }

    [Fact]
    public async Task A_failure_with_no_reason_says_the_client_reported_it_and_a_second_failure_is_skipped()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");
        var failed = new DownloadClientWebhookRequest("failed", dispatchId, null, null, null, null, null);

        await world.Webhook.HandleAsync(world.ClientId, failed, CancellationToken.None);
        var again = await world.Webhook.HandleAsync(world.ClientId, failed, CancellationToken.None);

        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("Download client reported failure.", dispatch!.ImportFailureMessage);
        Assert.Contains("skipped duplicate", again.Message, StringComparison.Ordinal);
        var timeline = await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        Assert.Single(timeline, e => e.EventType == "webhook-failed");
    }

    [Fact]
    public async Task An_event_Deluno_does_not_know_is_acknowledged_and_changes_nothing()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");
        var before = (await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None)).Count;

        var result = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest("paused", dispatchId, null, null, null, null, null), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Contains("no action taken", result.Message, StringComparison.Ordinal);
        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Null(dispatch!.ClientCompletedUtc);
        Assert.Null(dispatch.ImportStatus);
        Assert.Equal(before, (await world.Dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_dispatch_of_another_client_is_not_reached_by_this_clients_webhook()
    {
        using var world = await World.CreateAsync();
        var dispatchId = await world.SendAsync(existingId: "real-hash");

        var result = await world.Webhook.HandleAsync(
            "some-other-client", new DownloadClientWebhookRequest("completed", dispatchId, null, null, null, 10, null), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Null((await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly SqliteJobStore _jobs;
        private readonly string _libraryId;
        private readonly string _clientName;

        private readonly FixedTimeProvider _time;

        private World(TestStorage storage, SqliteJobStore jobs, SqliteDownloadDispatchesRepository dispatches, DownloadClientWebhookService webhook, string clientId, string clientName, FixedTimeProvider time)
        {
            _time = time;
            _storage = storage;
            _jobs = jobs;
            Dispatches = dispatches;
            Webhook = webhook;
            ClientId = clientId;
            _clientName = clientName;
            _libraryId = "library-1";
        }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public DownloadClientWebhookService Webhook { get; }

        public string ClientId { get; }

        public async Task<string> SendAsync(string? existingId)
        {
            return await _jobs.RecordDownloadDispatchAsync(
                _libraryId, "movies", "movie", "movie-1", Release, "Fixture", ClientId, _clientName, "sent", null,
                cancellationToken: CancellationToken.None,
                clientExternalId: existingId);
        }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var webhook = new DownloadClientWebhookService(dispatches, NullLogger<DownloadClientWebhookService>.Instance);
            return new World(storage, jobs, dispatches, webhook, client.Id, client.Name, time);
        }

        public void Dispose() => _storage.Dispose();
    }
}
