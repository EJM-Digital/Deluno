using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1062, James 30 Sep 2026: Deluno sends a release and the client answers that
/// it already has that exact torrent. With Deluno's own dispatch record for that
/// hash (any status, archived included) the copy is Deluno's and is followed.
/// Otherwise it was added outside Deluno: Deluno leaves it alone, fails that
/// release with a plain reason (which the search handlers blocklist and search
/// past, with an Activity line) and never adopts the item by name.
/// </summary>
public sealed class AClientThatAlreadyHasATorrentFromElsewhereIsLeftAloneTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string Release = "Arrival.2016.1080p.WEB";

    [Fact]
    public async Task A_torrent_Deluno_sent_before_and_has_since_archived_is_followed_as_normal()
    {
        using var world = await World.CreateAsync();
        var earlier = await world.RecordDispatchAsync(Release);
        await world.Dispatches.RecordDetectionAsync(earlier, Hash, 1, completed: true, CancellationToken.None);
        await world.Dispatches.ArchiveDispatchAsync(earlier, "imported", CancellationToken.None);
        var again = await world.RecordDispatchAsync(Release);

        var result = await world.GrabAsync(again);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task A_torrent_Deluno_did_not_send_is_left_alone_and_the_release_is_failed_with_the_reason()
    {
        using var world = await World.CreateAsync();
        var dispatch = await world.RecordDispatchAsync(Release);

        var result = await world.GrabAsync(dispatch);

        Assert.False(result.Succeeded);
        // The status the search handlers already blocklist, search past and write to Activity for.
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Equal($"{world.ClientName} already has this one from elsewhere.", result.Message);
        // Nothing was adopted: the dispatch never received the outside item's id.
        var stored = await world.Dispatches.GetDispatchAsync(dispatch, CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(stored!.TorrentHashOrItemId));
        Assert.Empty(await world.Dispatches.FindDispatchesByClientItemIdsAsync(world.ClientId, [Hash], CancellationToken.None));
    }

    [Fact]
    public async Task A_same_named_dispatch_with_no_id_does_not_make_the_outside_torrent_Delunos()
    {
        using var world = await World.CreateAsync();
        // Sent earlier under the same name, and the client never gave back an id.
        await world.RecordDispatchAsync(Release);
        var thisGrab = await world.RecordDispatchAsync(Release);

        var result = await world.GrabAsync(thisGrab);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly SqliteJobStore _jobs;
        private readonly DownloadClientGrabService _service;
        private readonly string _libraryId;
        private readonly string _clientName;

        private World(TestStorage storage, SqliteJobStore jobs, SqliteDownloadDispatchesRepository dispatches, DownloadClientGrabService service, string libraryId, string clientId, string clientName)
        {
            _storage = storage;
            _jobs = jobs;
            Dispatches = dispatches;
            _service = service;
            _libraryId = libraryId;
            ClientId = clientId;
            _clientName = clientName;
        }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public string ClientId { get; }

        public string ClientName => _clientName;

        public Task<string> RecordDispatchAsync(string release)
            => _jobs.RecordDownloadDispatchAsync(
                _libraryId, "movies", "movie", "movie-arrival", release, "Fixture", ClientId, _clientName, "planned", null,
                cancellationToken: CancellationToken.None);

        public Task<DownloadClientGrabResult> GrabAsync(string dispatchId)
            => _service.GrabAsync(
                ClientId,
                new DownloadClientGrabRequest(Release, "magnet:?xt=urn:btih:" + Hash, "movies", "movies", "Fixture", DispatchId: dispatchId),
                CancellationToken.None);

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
            var library = await new SqliteLibrariesRepository(storage.Factory, time).CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var service = new DownloadClientGrabService(
                connections,
                new DownloadClientRegistry([new ClientThatAlreadyHasIt()]),
                new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance),
                dispatches,
                new NullRealtimeEventPublisher());
            return new World(storage, jobs, dispatches, service, library.Id, client.Id, client.Name);
        }

        public void Dispose() => _storage.Dispose();
    }

    /// <summary>A torrent client that answers every grab with "I already have that exact torrent".</summary>
    private sealed class ClientThatAlreadyHasIt : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "qBittorrent already holds this release.")
            {
                ExternalId = Hash,
                IsDuplicateFollow = true
            });

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
