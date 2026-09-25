using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #662: the shared notify path behind import, upgrade, season-pack, rename
/// and manual removal - path mapping applied per server, one down server
/// never stopping another from being told, and a failure landing on that
/// server's own health rather than anywhere else.
/// </summary>
public sealed class MediaServerRefreshNotifierTests
{
    private static MediaServerConnection Server(string id, string kind, string healthStatus = "healthy")
        => new(id, kind, "Server " + id, "http://192.168.1.51:8096", "token", true, healthStatus, null, null, null, "Rig", "abc", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_path_is_translated_to_the_servers_own_view_before_the_client_is_called()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var repository = new FakeRepository(
            [server],
            [new MediaServerPathMappingItem("m1", "s1", "/mnt/media/Movies", "/data/Movies", true, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync(
            "movies",
            [new MediaServerPathChange("/mnt/media/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var change = Assert.Single(Assert.Single(client.Calls).Changes);
        Assert.Equal("/data/Movies/Sintel (2010)/Sintel.mkv", change.Path);
    }

    [Fact]
    public async Task A_path_with_no_mapping_is_sent_as_is()
    {
        var server = Server("s1", MediaServerKinds.Jellyfin);
        var repository = new FakeRepository([server], []);
        var client = new FakeClient(MediaServerKinds.Jellyfin);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Equal("/media/Movies/Sintel.mkv", Assert.Single(client.Calls[0].Changes).Path);
    }

    [Fact]
    public async Task One_down_server_never_stops_another_being_told()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var up = Server("up", MediaServerKinds.Jellyfin);
        var repository = new FakeRepository([down, up], []);
        var downClient = new FakeClient(MediaServerKinds.Plex, fail: true);
        var upClient = new FakeClient(MediaServerKinds.Jellyfin);
        var registry = new MultiRegistry(new() { [MediaServerKinds.Plex] = downClient, [MediaServerKinds.Jellyfin] = upClient });
        var notifier = new MediaServerRefreshNotifier(repository, registry, TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Single(downClient.Calls);
        Assert.Single(upClient.Calls);
    }

    [Fact]
    public async Task A_failed_notify_records_a_clearable_health_finding_on_that_server_only()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var repository = new FakeRepository([down], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var recorded = Assert.Single(repository.HealthRecords);
        Assert.Equal("down", recorded.Id);
        Assert.False(recorded.Result.Ok);
    }

    [Fact]
    public async Task A_successful_notify_clears_a_previous_failure_but_does_not_rewrite_a_healthy_server()
    {
        var recovering = Server("recovering", MediaServerKinds.Plex, healthStatus: "unreachable");
        var repository = new FakeRepository([recovering], []);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var recorded = Assert.Single(repository.HealthRecords);
        Assert.True(recorded.Result.Ok);

        repository.HealthRecords.Clear();
        var healthy = Server("healthy", MediaServerKinds.Plex, healthStatus: "healthy");
        var repository2 = new FakeRepository([healthy], []);
        var notifier2 = new MediaServerRefreshNotifier(repository2, new FakeRegistry(client), TimeProvider.System);

        await notifier2.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Empty(repository2.HealthRecords);
    }

    [Fact]
    public async Task No_changes_asks_nothing()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(new FakeRepository([server], []), new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [], CancellationToken.None);

        Assert.Empty(client.Calls);
    }

    private sealed record RefreshCall(string MediaType, IReadOnlyList<MediaServerPathChange> Changes);

    private sealed class FakeClient(string kind, bool fail = false) : IMediaServerClient
    {
        public string Kind => kind;
        public List<RefreshCall> Calls { get; } = [];

        public Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(MediaServerConnection connection, string mediaType, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(MediaServerConnection connection, MediaServerItem item, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRefresh>> RefreshAsync(
            MediaServerConnection connection, string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken)
        {
            Calls.Add(new RefreshCall(mediaType, changes));
            return Task.FromResult<MediaServerAnswer<MediaServerRefresh>>(fail
                ? new MediaServerAnswer<MediaServerRefresh>.Unreachable($"{connection.Name} is down.")
                : new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh("ok", false)));
        }

        public Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(MediaServerConnection connection, string serverPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(MediaServerConnection connection, MediaServerCollectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeRegistry(IMediaServerClient client) : IMediaServerClientRegistry
    {
        public IMediaServerClient? Find(string kind) => string.Equals(client.Kind, kind, StringComparison.OrdinalIgnoreCase) ? client : null;
    }

    private sealed class MultiRegistry(Dictionary<string, IMediaServerClient> clients) : IMediaServerClientRegistry
    {
        public IMediaServerClient? Find(string kind) => clients.GetValueOrDefault(kind);
    }

    private sealed class FakeRepository(IReadOnlyList<MediaServerConnection> servers, IReadOnlyList<MediaServerPathMappingItem> mappings) : IMediaServerRepository
    {
        public List<(string Id, MediaServerTestResult Result)> HealthRecords { get; } = [];

        public Task<IReadOnlyList<MediaServerConnection>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(servers);

        public Task<MediaServerConnection?> GetAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(servers.FirstOrDefault(server => server.Id == id));

        public Task<MediaServerConnection?> SaveAsync(string? id, SaveMediaServerRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RecordHealthAsync(string id, MediaServerTestResult result, CancellationToken cancellationToken)
        {
            HealthRecords.Add((id, result));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaServerPathMappingItem>> ListPathMappingsAsync(string? mediaServerId, CancellationToken cancellationToken)
            => Task.FromResult(mediaServerId is null ? mappings : (IReadOnlyList<MediaServerPathMappingItem>)mappings.Where(mapping => mapping.MediaServerId == mediaServerId).ToArray());

        public Task<MediaServerPathMappingItem?> CreatePathMappingAsync(string mediaServerId, CreateMediaServerPathMappingRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> DeletePathMappingAsync(string mediaServerId, string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
