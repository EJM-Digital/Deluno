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
/// a dispatch is bound to: an id already there stays, and a dispatch that has
/// none only takes the id the client itself shows under that release's name.
/// </summary>
public sealed class AWebhookCannotBindAnIdToADispatchTests
{
    private const string Release = "Sent.Movie.2026.1080p.WEB";

    [Fact]
    public async Task A_dispatch_that_has_an_id_keeps_it_whatever_hash_the_webhook_names()
    {
        using var world = await World.CreateAsync(clientShowsHash: "someone-elses-hash", clientShowsName: Release);
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
    public async Task A_dispatch_without_an_id_does_not_take_a_hash_the_client_shows_under_another_name()
    {
        using var world = await World.CreateAsync(clientShowsHash: "outside-hash", clientShowsName: "Something.Else.Entirely.2026");
        var dispatchId = await world.SendAsync(existingId: null);

        await world.Webhook.HandleAsync(
            world.ClientId,
            new DownloadClientWebhookRequest("completed", dispatchId, "outside-hash", null, null, 10, null),
            CancellationToken.None);

        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(dispatch!.TorrentHashOrItemId));
    }

    [Fact]
    public async Task A_dispatch_without_an_id_takes_the_hash_the_client_shows_under_its_own_release_name()
    {
        using var world = await World.CreateAsync(clientShowsHash: "sent-hash", clientShowsName: Release);
        var dispatchId = await world.SendAsync(existingId: null);

        await world.Webhook.HandleAsync(
            world.ClientId,
            new DownloadClientWebhookRequest("completed", dispatchId, "SENT-HASH", null, null, 10, null),
            CancellationToken.None);

        var dispatch = await world.Dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("SENT-HASH", dispatch!.TorrentHashOrItemId);
    }

    [Fact]
    public async Task A_name_alone_no_longer_reaches_a_dispatch_sent_days_ago()
    {
        using var world = await World.CreateAsync(clientShowsHash: "x", clientShowsName: Release);
        await world.SendAsync(existingId: null);
        world.Advance(TimeSpan.FromDays(2));

        var stale = await world.Webhook.HandleAsync(
            world.ClientId, new DownloadClientWebhookRequest("completed", null, null, Release, null, 10, null), CancellationToken.None);

        Assert.False(stale.Accepted);
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

        public void Advance(TimeSpan duration) => _time.Advance(duration);

        public async Task<string> SendAsync(string? existingId)
        {
            var id = await _jobs.RecordDownloadDispatchAsync(
                _libraryId, "movies", "movie", "movie-1", Release, "Fixture", ClientId, _clientName, "sent", null,
                cancellationToken: CancellationToken.None);
            if (existingId is not null)
            {
                await Dispatches.RecordClientItemIdAsync(id, existingId, CancellationToken.None);
            }

            return id;
        }

        public static async Task<World> CreateAsync(string clientShowsHash, string clientShowsName)
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
            var registry = new DownloadClientRegistry([new Client(client, clientShowsHash, clientShowsName)]);
            var webhook = new DownloadClientWebhookService(
                dispatches, NullLogger<DownloadClientWebhookService>.Instance, connections, registry, time);
            return new World(storage, jobs, dispatches, webhook, client.Id, client.Name, time);
        }

        public void Dispose() => _storage.Dispose();
    }

    private sealed class Client(DownloadClientItem client, string hash, string name) : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            var queue = new List<DownloadQueueItem>
            {
                new(hash, client.Id, client.Name, Protocol, "movies", name, name, "deluno-movies", DownloadQueueStatuses.Downloading,
                    0.4, 1, 60, 1024, 400, 0, "Fixture", null, capturedUtc)
            };
            return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, Protocol, null, "healthy", null, Capabilities, DownloadQueueSummary.Of(queue), queue, [], capturedUtc));
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem requested, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem requested, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem requested, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem requested, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
